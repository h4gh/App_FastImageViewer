using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace FastImageViewer.Services;

public enum ImageTransformType
{
    RotateLeft,
    RotateRight,
    FlipHorizontal,
    FlipVertical
}

public static class ImageTransformService
{
    public static BitmapSource ApplyTransform(BitmapSource source, ImageTransformType transformType)
    {
        Transform transform = transformType switch
        {
            ImageTransformType.RotateLeft => new RotateTransform(270),
            ImageTransformType.RotateRight => new RotateTransform(90),
            ImageTransformType.FlipHorizontal => new ScaleTransform(-1, 1),
            ImageTransformType.FlipVertical => new ScaleTransform(1, -1),
            _ => Transform.Identity
        };

        var transformed = new TransformedBitmap(source, transform);
        transformed.Freeze();
        return transformed;
    }

    public static BitmapSource CreateThumbnail(BitmapSource source, int targetWidth = 220)
    {
        if (source.PixelWidth <= targetWidth)
            return source;

        double scale = (double)targetWidth / source.PixelWidth;
        var thumb = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        thumb.Freeze();
        return thumb;
    }

    public static BitmapSource CropBitmap(BitmapSource source, Int32Rect rect)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, source.PixelWidth - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, source.PixelHeight - 1));
        int w = Math.Clamp(rect.Width, 1, source.PixelWidth - x);
        int h = Math.Clamp(rect.Height, 1, source.PixelHeight - y);

        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    public static void SaveBitmapToFile(BitmapSource source, string filePath, int quality = 95)
    {
        string fullPath = Path.GetFullPath(filePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        string tempPath = fullPath + ".tmp_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (ext == ".webp")
            {
                SaveAsWebp(source, tempPath, quality);
            }
            else
            {
                BitmapEncoder encoder = ext switch
                {
                    ".png" or ".ico" => new PngBitmapEncoder(),
                    ".gif" => new GifBitmapEncoder(),
                    ".bmp" => new BmpBitmapEncoder(),
                    ".tif" or ".tiff" => new TiffBitmapEncoder(),
                    _ => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) }
                };

                encoder.Frames.Add(BitmapFrame.Create(source));
                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(fs);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void SaveAsWebp(BitmapSource source, string outputPath, int quality)
    {
        BitmapSource bgraSource = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgraSource.PixelWidth;
        int height = bgraSource.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        bgraSource.CopyPixels(pixels, stride, 0);

        using var image = Image.LoadPixelData<Bgra32>(pixels, width, height);
        var encoder = new WebpEncoder
        {
            Quality = Math.Clamp(quality, 1, 100),
            FileFormat = quality >= 100 ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy
        };
        image.Save(outputPath, encoder);
    }
}
