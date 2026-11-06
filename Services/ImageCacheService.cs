using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FastImageViewer.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FastImageViewer.Services;

public sealed class ImageCacheService
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif",
        ".png", ".webp", ".gif",
        ".bmp", ".tif", ".tiff",
        ".ico", ".heic", ".heif", ".avif"
    };

    public const string OpenFileDialogFilter =
        "Všechny podporované obrázky (*.jpg;*.jpeg;*.jfif;*.png;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.ico;*.heic;*.heif;*.avif)|*.jpg;*.jpeg;*.jfif;*.png;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.ico;*.heic;*.heif;*.avif|" +
        "JPEG (*.jpg;*.jpeg;*.jfif)|*.jpg;*.jpeg;*.jfif|" +
        "PNG (*.png)|*.png|" +
        "WebP (*.webp)|*.webp|" +
        "BMP (*.bmp)|*.bmp|" +
        "TIFF (*.tif;*.tiff)|*.tif;*.tiff|" +
        "GIF (*.gif)|*.gif|" +
        "Ikony (*.ico)|*.ico|" +
        "HEIC / AVIF (*.heic;*.heif;*.avif)|*.heic;*.heif;*.avif|" +
        "Všechny soubory (*.*)|*.*";

    public const string SaveFileDialogFilter =
        "JPEG obrázek (*.jpg;*.jpeg;*.jfif)|*.jpg;*.jpeg;*.jfif|" +
        "PNG obrázek (*.png)|*.png|" +
        "WebP obrázek (*.webp)|*.webp|" +
        "BMP obrázek (*.bmp)|*.bmp|" +
        "TIFF obrázek (*.tif;*.tiff)|*.tif;*.tiff|" +
        "GIF obrázek (*.gif)|*.gif";

    public static int GetSaveFilterIndex(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".ico" => 2,
        ".webp" => 3,
        ".bmp" => 4,
        ".tif" or ".tiff" => 5,
        ".gif" => 6,
        _ => 1
    };

    public static string NormalizeWritableExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".ico" => ".png",
        ".heic" or ".heif" or ".avif" => ".jpg",
        "" => ".png",
        var ext => ext
    };

    private const int MaxCacheSize = 9;
    private const int ThumbnailDecodeWidth = 220;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, BitmapSource> _fullImageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lruOrder = new();
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inFlightLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _thumbnailSemaphore = new(Math.Max(2, Environment.ProcessorCount));

    public static bool IsSupportedFile(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
    }

    public static List<string> GetImagesInFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return [];

        try
        {
            var files = Directory.EnumerateFiles(folderPath)
                .Where(IsSupportedFile)
                .ToList();

            files.Sort((a, b) => NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));
            return files;
        }
        catch
        {
            return [];
        }
    }

    public BitmapSource? TryGetCached(string filePath)
    {
        lock (_cacheLock)
        {
            if (_fullImageCache.TryGetValue(filePath, out var bitmap))
            {
                TouchLru(filePath);
                return bitmap;
            }
            return null;
        }
    }

    public Task<BitmapSource?> GetOrLoadImageAsync(string filePath)
    {
        var cached = TryGetCached(filePath);
        if (cached != null)
            return Task.FromResult<BitmapSource?>(cached);

        return _inFlightLoads.GetOrAdd(filePath, path => Task.Run(() =>
        {
            try
            {
                var loaded = DecodeImageFromFile(path, decodeWidth: null);
                if (loaded != null)
                {
                    AddToCache(path, loaded);
                }
                return loaded;
            }
            finally
            {
                _inFlightLoads.TryRemove(path, out _);
            }
        }));
    }

    public void PrefetchAround(IReadOnlyList<ImageItem> items, int currentIndex)
    {
        if (items.Count == 0 || currentIndex < 0 || currentIndex >= items.Count)
            return;

        // Priority order: +1, -1, +2, -2
        int[] offsets = [1, -1, 2, -2];
        foreach (int offset in offsets)
        {
            int targetIdx = currentIndex + offset;
            if (targetIdx >= 0 && targetIdx < items.Count)
            {
                string path = items[targetIdx].FilePath;
                if (TryGetCached(path) == null)
                {
                    _ = GetOrLoadImageAsync(path);
                }
            }
        }
    }

    public async Task EnsureThumbnailLoadedAsync(ImageItem item, CancellationToken cancellationToken = default)
    {
        if (item.Thumbnail != null || item.IsLoadingThumbnail)
            return;

        item.IsLoadingThumbnail = true;
        try
        {
            await _thumbnailSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var thumb = DecodeImageFromFile(item.FilePath, ThumbnailDecodeWidth);
                if (thumb != null)
                {
                    item.Thumbnail = thumb;
                }
            }
            finally
            {
                _thumbnailSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            item.IsLoadingThumbnail = false;
        }
        catch
        {
            item.IsLoadingThumbnail = false;
        }
    }

    public void Invalidate(string filePath)
    {
        lock (_cacheLock)
        {
            _fullImageCache.Remove(filePath);
            _lruOrder.Remove(filePath);
        }
    }

    public void UpdateCached(string filePath, BitmapSource bitmap)
    {
        AddToCache(filePath, bitmap);
    }

    public void Clear()
    {
        lock (_cacheLock)
        {
            _fullImageCache.Clear();
            _lruOrder.Clear();
        }
    }

    private void AddToCache(string filePath, BitmapSource bitmap)
    {
        lock (_cacheLock)
        {
            if (_fullImageCache.ContainsKey(filePath))
            {
                _fullImageCache[filePath] = bitmap;
                TouchLru(filePath);
                return;
            }

            while (_fullImageCache.Count >= MaxCacheSize && _lruOrder.Last != null)
            {
                string oldest = _lruOrder.Last.Value;
                _lruOrder.RemoveLast();
                _fullImageCache.Remove(oldest);
            }

            _fullImageCache[filePath] = bitmap;
            _lruOrder.AddFirst(filePath);
        }
    }

    private void TouchLru(string filePath)
    {
        _lruOrder.Remove(filePath);
        _lruOrder.AddFirst(filePath);
    }

    public static BitmapSource? DecodeFullBitmap(string filePath) => DecodeImageFromFile(filePath, decodeWidth: null);

    private static BitmapSource? DecodeImageFromFile(string filePath, int? decodeWidth)
    {
        if (!File.Exists(filePath))
            return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch
        {
            return null;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        // For .ico files, pick the highest-resolution frame from the icon directory
        if (ext == ".ico")
        {
            try
            {
                using var icoStream = new MemoryStream(bytes);
                var icoDecoder = new IconBitmapDecoder(icoStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bestFrame = icoDecoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                if (bestFrame != null)
                {
                    bestFrame.Freeze();
                    return bestFrame;
                }
            }
            catch
            {
                // Fall through to standard decoder
            }
        }

        try
        {
            ushort orientation = ReadExifOrientation(bytes);

            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth.HasValue)
            {
                bitmap.DecodePixelWidth = decodeWidth.Value;
            }
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();

            if (orientation is > 1 and <= 8)
            {
                var transformed = ApplyExifOrientation(bitmap, orientation);
                transformed.Freeze();
                return transformed;
            }

            return bitmap;
        }
        catch
        {
            // Fallback to ImageSharp if WIC could not decode a particular WebP / TIFF / BMP sub-format
            try
            {
                using var img = Image.Load<Bgra32>(bytes);
                img.Mutate(x => x.AutoOrient());
                if (decodeWidth.HasValue && img.Width > decodeWidth.Value)
                {
                    int targetW = decodeWidth.Value;
                    int targetH = Math.Max(1, (int)Math.Round(img.Height * (targetW / (double)img.Width)));
                    img.Mutate(x => x.Resize(targetW, targetH));
                }

                int w = img.Width;
                int h = img.Height;
                int stride = w * 4;
                byte[] pixelBytes = new byte[h * stride];
                img.CopyPixelDataTo(pixelBytes);

                var bmpSource = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixelBytes, stride);
                bmpSource.Freeze();
                return bmpSource;
            }
            catch
            {
                return null;
            }
        }
    }

    private static ushort ReadExifOrientation(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is BitmapMetadata metadata)
            {
                if (metadata.ContainsQuery("System.Photo.Orientation"))
                {
                    object? val = metadata.GetQuery("System.Photo.Orientation");
                    if (val is ushort u)
                        return u;
                }
            }
        }
        catch
        {
            // Ignore metadata errors (e.g. formats without EXIF)
        }
        return 1;
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource source, ushort orientation)
    {
        var transformGroup = new TransformGroup();
        switch (orientation)
        {
            case 2: // Flip horizontal
                transformGroup.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 3: // Rotate 180
                transformGroup.Children.Add(new RotateTransform(180));
                break;
            case 4: // Flip vertical
                transformGroup.Children.Add(new ScaleTransform(1, -1));
                break;
            case 5: // Transpose
                transformGroup.Children.Add(new RotateTransform(90));
                transformGroup.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 6: // Rotate 90 CW
                transformGroup.Children.Add(new RotateTransform(90));
                break;
            case 7: // Transverse
                transformGroup.Children.Add(new RotateTransform(270));
                transformGroup.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 8: // Rotate 270 CW
                transformGroup.Children.Add(new RotateTransform(270));
                break;
            default:
                return source;
        }

        return new TransformedBitmap(source, transformGroup);
    }
}
