using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FastImageViewer.Services;

public enum BatchOutputFormat
{
    Jpeg,
    Webp,
    Png,
    Bmp,
    Tiff
}

public enum BatchResizeMode
{
    LongEdgePixels,
    Percentage
}

public sealed record BatchConvertOptions(
    BatchOutputFormat OutputFormat,
    int Quality,
    bool EnableResize,
    BatchResizeMode ResizeMode,
    int ResizeValue,
    string OutputDirectory);

public sealed record BatchProgressReport(
    int CompletedCount,
    int TotalCount,
    string CurrentFileName,
    int ErrorCount);

public static class BatchConversionService
{
    public static async Task<(int SuccessCount, int ErrorCount)> RunBatchAsync(
        IReadOnlyList<string> inputFiles,
        BatchConvertOptions options,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (inputFiles.Count == 0)
            return (0, 0);

        string outDir = Path.GetFullPath(options.OutputDirectory);
        if (!Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        int total = inputFiles.Count;
        int completed = 0;
        int errors = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(inputFiles, parallelOptions, async (filePath, token) =>
        {
            string fileName = Path.GetFileName(filePath);
            try
            {
                await ConvertSingleFileAsync(filePath, outDir, options, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Interlocked.Increment(ref errors);
            }
            finally
            {
                int done = Interlocked.Increment(ref completed);
                progress?.Report(new BatchProgressReport(done, total, fileName, Volatile.Read(ref errors)));
            }
        }).ConfigureAwait(false);

        return (completed - errors, errors);
    }

    private static async Task ConvertSingleFileAsync(
        string inputFilePath,
        string outputDirectory,
        BatchConvertOptions options,
        CancellationToken token)
    {
        using Image image = await LoadImageWithWicFallbackAsync(inputFilePath, token).ConfigureAwait(false);

        if (options.EnableResize)
        {
            var (newWidth, newHeight) = CalculateTargetDimensions(image.Width, image.Height, options);
            if (newWidth != image.Width || newHeight != image.Height)
            {
                image.Mutate(x => x.Resize(newWidth, newHeight, KnownResamplers.Lanczos3));
            }
        }

        string targetExt = options.OutputFormat switch
        {
            BatchOutputFormat.Webp => ".webp",
            BatchOutputFormat.Png => ".png",
            BatchOutputFormat.Bmp => ".bmp",
            BatchOutputFormat.Tiff => ".tif",
            _ => ".jpg"
        };

        string baseName = Path.GetFileNameWithoutExtension(inputFilePath);
        string destPath = Path.Combine(outputDirectory, baseName + targetExt);

        // Prevent overwriting the exact source file while converting into the same folder
        if (string.Equals(Path.GetFullPath(inputFilePath), destPath, StringComparison.OrdinalIgnoreCase))
        {
            destPath = Path.Combine(outputDirectory, $"{baseName}_converted{targetExt}");
        }

        IImageEncoder encoder = CreateEncoder(options.OutputFormat, options.Quality);
        await image.SaveAsync(destPath, encoder, token).ConfigureAwait(false);
    }

    private static async Task<Image> LoadImageWithWicFallbackAsync(string inputFilePath, CancellationToken token)
    {
        string ext = Path.GetExtension(inputFilePath).ToLowerInvariant();
        if (ext is not (".ico" or ".heic" or ".heif" or ".avif"))
        {
            try
            {
                var img = await Image.LoadAsync(inputFilePath, token).ConfigureAwait(false);
                img.Mutate(x => x.AutoOrient());
                return img;
            }
            catch
            {
                // Fall through to WIC decoder
            }
        }

        BitmapSource? bmp = ImageCacheService.DecodeFullBitmap(inputFilePath)
            ?? throw new InvalidDataException($"Nelze načíst obrázek: {inputFilePath}");

        BitmapSource bgraSource = bmp.Format == PixelFormats.Bgra32
            ? bmp
            : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);

        int width = bgraSource.PixelWidth;
        int height = bgraSource.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        bgraSource.CopyPixels(pixels, stride, 0);

        return Image.LoadPixelData<Bgra32>(pixels, width, height);
    }

    private static (int Width, int Height) CalculateTargetDimensions(
        int originalWidth,
        int originalHeight,
        BatchConvertOptions options)
    {
        if (originalWidth <= 0 || originalHeight <= 0)
            return (originalWidth, originalHeight);

        if (options.ResizeMode == BatchResizeMode.LongEdgePixels)
        {
            int targetLongEdge = Math.Max(16, options.ResizeValue);
            int currentLongEdge = Math.Max(originalWidth, originalHeight);

            // Shrink large images to targetLongEdge; keep smaller images at original size
            if (currentLongEdge <= targetLongEdge)
                return (originalWidth, originalHeight);

            double scale = (double)targetLongEdge / currentLongEdge;
            int w = Math.Max(1, (int)Math.Round(originalWidth * scale));
            int h = Math.Max(1, (int)Math.Round(originalHeight * scale));
            return (w, h);
        }
        else
        {
            int pct = Math.Clamp(options.ResizeValue, 1, 500);
            if (pct == 100)
                return (originalWidth, originalHeight);

            double scale = pct / 100.0;
            int w = Math.Max(1, (int)Math.Round(originalWidth * scale));
            int h = Math.Max(1, (int)Math.Round(originalHeight * scale));
            return (w, h);
        }
    }

    private static IImageEncoder CreateEncoder(BatchOutputFormat format, int quality)
    {
        int clampedQuality = Math.Clamp(quality, 10, 100);
        return format switch
        {
            BatchOutputFormat.Webp => new WebpEncoder
            {
                Quality = clampedQuality,
                FileFormat = clampedQuality >= 100 ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy
            },
            BatchOutputFormat.Png => new PngEncoder(),
            BatchOutputFormat.Bmp => new BmpEncoder(),
            BatchOutputFormat.Tiff => new TiffEncoder(),
            _ => new JpegEncoder
            {
                Quality = clampedQuality
            }
        };
    }
}
