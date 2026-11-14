using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace FastImageViewer.Services;

public sealed class PhotoDetailedMetadata
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public string FileTypeDescription { get; init; } = "—";
    public string FileSizeFormatted { get; init; } = "—";
    public string FileDateFormatted { get; init; } = "—";
    public string DimensionsAndMpFormatted { get; init; } = "—";
    public string PrintSizeAndDpiFormatted { get; init; } = "—";

    public string QuickSummaryText { get; init; } = string.Empty;

    public string Make { get; init; } = "—";
    public string Model { get; init; } = "—";
    public string LensModel { get; init; } = "—";
    public string Software { get; init; } = "—";
    public string DateTaken { get; init; } = "—";
    public string ExposureTime { get; init; } = "—";
    public string FNumber { get; init; } = "—";
    public string IsoSpeed { get; init; } = "—";
    public string ExposureProgram { get; init; } = "—";
    public string MeteringMode { get; init; } = "—";
    public string ExposureBias { get; init; } = "—";
    public string Flash { get; init; } = "—";
    public string FocalLength { get; init; } = "—";
    public string FocalLength35mm { get; init; } = "—";
    public string WhiteBalanceAndColorSpace { get; init; } = "—";
    public string OrientationText { get; init; } = "—";

    public bool HasGps { get; init; }
    public double GpsLatitude { get; init; }
    public double GpsLongitude { get; init; }
    public string GpsDisplayText { get; init; } = "Bez GPS souřadnic";
    public string MapyCzUrl { get; init; } = string.Empty;
    public string GoogleMapsUrl { get; init; } = string.Empty;
    public string GpsClipboardText { get; init; } = string.Empty;
}

public static class ExifMetadataService
{
    private const int MaxCacheEntries = 256;
    private static readonly ConcurrentDictionary<string, PhotoDetailedMetadata> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static PhotoDetailedMetadata GetMetadata(string filePath, int fallbackPixelWidth = 0, int fallbackPixelHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new PhotoDetailedMetadata
            {
                FileName = Path.GetFileName(filePath) ?? string.Empty,
                FilePath = filePath ?? string.Empty,
                FolderPath = Path.GetDirectoryName(filePath) ?? string.Empty
            };
        }

        DateTime lastWriteUtc = DateTime.MinValue;
        long fileSizeBytes = 0;
        DateTime lastWriteLocal = DateTime.MinValue;
        try
        {
            var fi = new FileInfo(filePath);
            fileSizeBytes = fi.Length;
            lastWriteUtc = fi.LastWriteTimeUtc;
            lastWriteLocal = fi.LastWriteTime;
        }
        catch
        {
            // Ignore FileInfo errors
        }

        string cacheKey = $"{filePath}|{lastWriteUtc.Ticks}|{fallbackPixelWidth}x{fallbackPixelHeight}";
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (Cache.Count >= MaxCacheEntries)
        {
            Cache.Clear();
        }

        PhotoDetailedMetadata result = ReadMetadataFromFile(filePath, fileSizeBytes, lastWriteLocal, fallbackPixelWidth, fallbackPixelHeight);
        Cache[cacheKey] = result;
        return result;
    }

    public static void InvalidateFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        string prefix = filePath + "|";
        foreach (string key in Cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Cache.TryRemove(key, out _);
            }
        }
    }

    private static PhotoDetailedMetadata ReadMetadataFromFile(
        string filePath,
        long fileSizeBytes,
        DateTime lastWriteLocal,
        int fallbackPixelWidth,
        int fallbackPixelHeight)
    {
        int width = 0;
        int height = 0;
        int bitsPerPixel = 0;
        double dpiX = 0;
        double dpiY = 0;
        string detectedFormatName = string.Empty;
        ExifProfile? exif = null;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ImageInfo info = Image.Identify(fs);
            width = info.Width;
            height = info.Height;
            bitsPerPixel = info.PixelType.BitsPerPixel;
            detectedFormatName = info.Metadata.DecodedImageFormat?.Name ?? string.Empty;
            exif = info.Metadata.ExifProfile;

            double factor = info.Metadata.ResolutionUnits switch
            {
                PixelResolutionUnit.PixelsPerCentimeter => 2.54,
                PixelResolutionUnit.PixelsPerMeter => 0.0254,
                _ => 1.0
            };

            if (info.Metadata.HorizontalResolution > 0 && info.Metadata.ResolutionUnits != PixelResolutionUnit.AspectRatio)
                dpiX = info.Metadata.HorizontalResolution * factor;
            if (info.Metadata.VerticalResolution > 0 && info.Metadata.ResolutionUnits != PixelResolutionUnit.AspectRatio)
                dpiY = info.Metadata.VerticalResolution * factor;
        }
        catch
        {
            // Fallback to WIC metadata reader below (e.g. HEIC, AVIF, ICO)
        }

        BitmapMetadata? wicMeta = null;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                BitmapFrame frame = decoder.Frames[0];
                if (width <= 0) width = frame.PixelWidth;
                if (height <= 0) height = frame.PixelHeight;
                if (bitsPerPixel <= 0 && frame.Format.BitsPerPixel > 0) bitsPerPixel = frame.Format.BitsPerPixel;
                if (dpiX <= 0 && frame.DpiX > 0) dpiX = frame.DpiX;
                if (dpiY <= 0 && frame.DpiY > 0) dpiY = frame.DpiY;
                wicMeta = frame.Metadata as BitmapMetadata;
            }
        }
        catch
        {
            // Ignore WIC metadata errors
        }

        ushort orientationCode = ReadOrientation(exif, wicMeta);
        if (orientationCode is 5 or 6 or 7 or 8 && width > 0 && height > 0)
        {
            (width, height) = (height, width);
        }

        if (width <= 0 && fallbackPixelWidth > 0) width = fallbackPixelWidth;
        if (height <= 0 && fallbackPixelHeight > 0) height = fallbackPixelHeight;

        if (dpiX <= 1) dpiX = 72;
        if (dpiY <= 1) dpiY = dpiX;

        // Read EXIF fields
        string make = FirstNonEmpty(
            ReadExifString(exif, ExifTag.Make),
            ReadWicString(wicMeta, "System.Photo.CameraManufacturer"));

        string model = FirstNonEmpty(
            ReadExifString(exif, ExifTag.Model),
            ReadWicString(wicMeta, "System.Photo.CameraModel"));

        string lensModel = FirstNonEmpty(
            ReadExifString(exif, ExifTag.LensModel),
            ReadWicString(wicMeta, "System.Photo.LensModel"));

        string software = FirstNonEmpty(
            ReadExifString(exif, ExifTag.Software),
            ReadWicString(wicMeta, "/app1/ifd/{ushort=305}"));

        string dateTakenRaw = FirstNonEmpty(
            ReadExifString(exif, ExifTag.DateTimeOriginal),
            ReadExifString(exif, ExifTag.DateTimeDigitized),
            ReadExifString(exif, ExifTag.DateTime));
        string dateTaken = FormatExifDate(dateTakenRaw);
        if (dateTaken == "—")
        {
            dateTaken = FormatWicDate(wicMeta, "System.Photo.DateTaken");
        }

        string exposureTime = ReadExposureTime(exif, wicMeta);
        string fNumber = ReadFNumber(exif, wicMeta);
        string isoSpeed = ReadIso(exif, wicMeta);
        string exposureProgram = ReadExposureProgram(exif, wicMeta);
        string meteringMode = ReadMeteringMode(exif, wicMeta);
        string exposureBias = ReadExposureBias(exif, wicMeta);
        string flash = ReadFlash(exif, wicMeta);
        string focalLength = ReadFocalLength(exif, wicMeta);
        string focalLength35mm = ReadFocalLength35mm(exif, wicMeta);

        string whiteBalance = ReadWhiteBalance(exif);
        string colorSpace = ReadColorSpace(exif);
        string wbAndCs = (whiteBalance, colorSpace) switch
        {
            ("—", "—") => "—",
            (_, "—") => whiteBalance,
            ("—", _) => colorSpace,
            _ => $"{whiteBalance}  •  {colorSpace}"
        };

        string orientationText = orientationCode switch
        {
            1 => "Normal (1)",
            2 => "Flip H (2)",
            3 => "Rotate 180° (3)",
            4 => "Flip V (4)",
            5 => "Transpose (5)",
            6 => "Rotate 90° CW (6)",
            7 => "Transverse (7)",
            8 => "Rotate 270° CW (8)",
            _ => "—"
        };

        // GPS
        bool hasGps = TryReadGps(exif, wicMeta, out double gpsLat, out double gpsLon, out double? gpsAlt);
        string gpsDisplay = "Bez GPS souřadnic";
        string mapyUrl = string.Empty;
        string googleUrl = string.Empty;
        string gpsClip = string.Empty;

        if (hasGps)
        {
            string latDir = gpsLat >= 0 ? "N" : "S";
            string lonDir = gpsLon >= 0 ? "E" : "W";
            string latAbs = Math.Abs(gpsLat).ToString("0.000000", CultureInfo.InvariantCulture);
            string lonAbs = Math.Abs(gpsLon).ToString("0.000000", CultureInfo.InvariantCulture);
            gpsDisplay = $"{latAbs} {latDir}, {lonAbs} {lonDir}";
            if (gpsAlt.HasValue)
            {
                gpsDisplay += $"  •  {Math.Round(gpsAlt.Value)} m n. m.";
            }

            string latSigned = gpsLat.ToString("0.000000", CultureInfo.InvariantCulture);
            string lonSigned = gpsLon.ToString("0.000000", CultureInfo.InvariantCulture);
            mapyUrl = $"https://mapy.cz/zakladni?x={lonSigned}&y={latSigned}&z=17&source=coor&id={lonSigned}%2C{latSigned}";
            googleUrl = $"https://www.google.com/maps/search/?api=1&query={latSigned},{lonSigned}";
            gpsClip = $"{latSigned}, {lonSigned}";
        }

        // Quick summary bar
        var summaryParts = new List<string>(5);
        if (fNumber != "—") summaryParts.Add(fNumber);
        if (exposureTime != "—") summaryParts.Add(exposureTime);
        if (isoSpeed != "—") summaryParts.Add(isoSpeed);
        if (focalLength35mm != "—")
            summaryParts.Add(focalLength35mm);
        else if (focalLength != "—")
            summaryParts.Add(focalLength);
        if (exposureBias != "—") summaryParts.Add(exposureBias);

        // File & Attributes strings
        string ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrEmpty(detectedFormatName))
        {
            detectedFormatName = ext switch
            {
                "JPG" or "JPEG" or "JFIF" => "JPEG",
                _ => string.IsNullOrEmpty(ext) ? "Obrázek" : ext
            };
        }

        string fileTypeDesc = detectedFormatName;
        if (!string.IsNullOrEmpty(ext) && !string.Equals(detectedFormatName, ext, StringComparison.OrdinalIgnoreCase))
            fileTypeDesc += $" ({ext})";
        if (colorSpace != "—")
            fileTypeDesc += $"  •  {colorSpace}";
        if (bitsPerPixel > 0)
            fileTypeDesc += $"  •  {bitsPerPixel} bit";

        string fileSizeFormatted;
        if (fileSizeBytes >= 1024 * 1024)
        {
            fileSizeFormatted = string.Format(CultureInfo.InvariantCulture, "{0:F2} MB ({1:N0} B)", fileSizeBytes / (1024.0 * 1024.0), fileSizeBytes);
        }
        else if (fileSizeBytes >= 1024)
        {
            fileSizeFormatted = string.Format(CultureInfo.InvariantCulture, "{0:F0} KB ({1:N0} B)", fileSizeBytes / 1024.0, fileSizeBytes);
        }
        else
        {
            fileSizeFormatted = $"{fileSizeBytes} B";
        }

        string fileDateFormatted = lastWriteLocal == DateTime.MinValue
            ? "—"
            : lastWriteLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        string dimensionsFormatted = "—";
        string printSizeFormatted = "—";
        if (width > 0 && height > 0)
        {
            double mp = (width * (double)height) / 1_000_000.0;
            int gcd = GreatestCommonDivisor(width, height);
            int rw = width / gcd;
            int rh = height / gcd;
            string ratioStr = (rw <= 32 && rh <= 32)
                ? $"{rw}:{rh}"
                : string.Format(CultureInfo.InvariantCulture, "{0:0.##}:1", width / (double)height);
            dimensionsFormatted = string.Format(CultureInfo.InvariantCulture, "{0} × {1} px ({2:F2} MP)  •  {3}", width, height, mp, ratioStr);

            double inchesW = width / dpiX;
            double inchesH = height / dpiY;
            double cmW = inchesW * 2.54;
            double cmH = inchesH * 2.54;
            printSizeFormatted = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F1} × {1:F1} cm ({2:F1} × {3:F1} in)  •  {4:0}×{5:0} DPI",
                cmW, cmH, inchesW, inchesH, dpiX, dpiY);
        }

        return new PhotoDetailedMetadata
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            FolderPath = Path.GetDirectoryName(filePath) ?? string.Empty,
            FileTypeDescription = fileTypeDesc,
            FileSizeFormatted = fileSizeFormatted,
            FileDateFormatted = fileDateFormatted,
            DimensionsAndMpFormatted = dimensionsFormatted,
            PrintSizeAndDpiFormatted = printSizeFormatted,
            QuickSummaryText = string.Join("  •  ", summaryParts),
            Make = make,
            Model = model,
            LensModel = lensModel,
            Software = software,
            DateTaken = dateTaken,
            ExposureTime = exposureTime,
            FNumber = fNumber,
            IsoSpeed = isoSpeed,
            ExposureProgram = exposureProgram,
            MeteringMode = meteringMode,
            ExposureBias = exposureBias,
            Flash = flash,
            FocalLength = focalLength,
            FocalLength35mm = focalLength35mm,
            WhiteBalanceAndColorSpace = wbAndCs,
            OrientationText = orientationText,
            HasGps = hasGps,
            GpsLatitude = gpsLat,
            GpsLongitude = gpsLon,
            GpsDisplayText = gpsDisplay,
            MapyCzUrl = mapyUrl,
            GoogleMapsUrl = googleUrl,
            GpsClipboardText = gpsClip
        };
    }

    private static object? GetRawExif(ExifProfile? exif, ExifTag targetTag)
    {
        if (exif == null)
            return null;

        foreach (IExifValue val in exif.Values)
        {
            if (Equals(val.Tag, targetTag))
                return val.GetValue();
        }
        return null;
    }

    private static string ReadExifString(ExifProfile? exif, ExifTag tag)
    {
        object? raw = GetRawExif(exif, tag);
        if (raw is string s)
            return s.Replace("\0", string.Empty).Trim();
        return string.Empty;
    }

    private static ushort? ReadExifUShort(ExifProfile? exif, ExifTag tag)
    {
        object? raw = GetRawExif(exif, tag);
        return raw switch
        {
            ushort u => u,
            short s when s >= 0 => (ushort)s,
            uint ui => (ushort)ui,
            int i when i >= 0 => (ushort)i,
            byte b => b,
            ushort[] arr when arr.Length > 0 => arr[0],
            _ => null
        };
    }

    private static double? ReadExifDouble(ExifProfile? exif, ExifTag tag)
    {
        object? raw = GetRawExif(exif, tag);
        return raw switch
        {
            Rational r when r.Denominator != 0 => r.ToDouble(),
            SignedRational sr when sr.Denominator != 0 => sr.ToDouble(),
            Rational[] ra when ra.Length > 0 && ra[0].Denominator != 0 => ra[0].ToDouble(),
            SignedRational[] sra when sra.Length > 0 && sra[0].Denominator != 0 => sra[0].ToDouble(),
            double d => d,
            float f => f,
            ushort u => u,
            uint ui => ui,
            int i => i,
            _ => null
        };
    }

    private static string ReadWicString(BitmapMetadata? meta, string query)
    {
        if (meta == null)
            return string.Empty;
        try
        {
            if (meta.ContainsQuery(query) && meta.GetQuery(query) is string s)
                return s.Replace("\0", string.Empty).Trim();
        }
        catch
        {
            // Ignore WIC query errors
        }
        return string.Empty;
    }

    private static object? ReadWicRaw(BitmapMetadata? meta, string query)
    {
        if (meta == null)
            return null;
        try
        {
            if (meta.ContainsQuery(query))
                return meta.GetQuery(query);
        }
        catch
        {
            // Ignore WIC query errors
        }
        return null;
    }

    private static ushort ReadOrientation(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        ushort? u = ReadExifUShort(exif, ExifTag.Orientation);
        if (u.HasValue && u.Value >= 1 && u.Value <= 8)
            return u.Value;

        object? wicVal = ReadWicRaw(wicMeta, "System.Photo.Orientation");
        if (wicVal is ushort wu && wu >= 1 && wu <= 8)
            return wu;

        return 1;
    }

    private static string FormatExifDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "—";

        string[] formats = ["yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss.fff"];
        if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return raw.Trim();
    }

    private static string FormatWicDate(BitmapMetadata? wicMeta, string query)
    {
        object? raw = ReadWicRaw(wicMeta, query);
        if (raw is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (raw is string s && !string.IsNullOrWhiteSpace(s))
            return FormatExifDate(s);
        return "—";
    }

    private static string ReadExposureTime(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        object? raw = GetRawExif(exif, ExifTag.ExposureTime);
        if (raw is Rational r && r.Denominator != 0 && r.Numerator > 0)
        {
            double sec = r.ToDouble();
            return FormatShutterSpeed(sec, r.Numerator, r.Denominator);
        }

        if (ReadWicRaw(wicMeta, "System.Photo.ExposureTime") is double wd && wd > 0)
        {
            return FormatShutterSpeed(wd, 0, 0);
        }

        return "—";
    }

    private static string FormatShutterSpeed(double sec, uint num, uint den)
    {
        if (sec <= 0)
            return "—";
        if (num == 1 && den > 1)
            return $"1/{den} s";
        if (sec < 0.5)
        {
            double inv = 1.0 / sec;
            return Math.Abs(inv - Math.Round(inv)) < 0.05
                ? string.Format(CultureInfo.InvariantCulture, "1/{0:0} s", Math.Round(inv))
                : string.Format(CultureInfo.InvariantCulture, "1/{0:0.#} s", inv);
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} s", sec);
    }

    private static string ReadFNumber(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        double? val = ReadExifDouble(exif, ExifTag.FNumber);
        if (!val.HasValue && ReadWicRaw(wicMeta, "System.Photo.FNumber") is double wd)
            val = wd;

        if (val.HasValue && val.Value > 0)
            return string.Format(CultureInfo.InvariantCulture, "f/{0:0.##}", val.Value);

        return "—";
    }

    private static string ReadIso(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        ushort? iso = ReadExifUShort(exif, ExifTag.ISOSpeedRatings) ?? ReadExifUShort(exif, ExifTag.ISOSpeed);
        if (!iso.HasValue)
        {
            object? raw = ReadWicRaw(wicMeta, "System.Photo.ISOSpeed");
            if (raw is ushort u) iso = u;
            else if (raw is int i && i > 0) iso = (ushort)i;
        }

        return iso.HasValue && iso.Value > 0 ? $"ISO {iso.Value}" : "—";
    }

    private static string ReadExposureProgram(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        ushort? code = ReadExifUShort(exif, ExifTag.ExposureProgram);
        if (!code.HasValue && ReadWicRaw(wicMeta, "System.Photo.ExposureProgram") is ushort wc)
            code = wc;

        return code switch
        {
            0 => "Not defined",
            1 => "Manual (M)",
            2 => "Normal program",
            3 => "Aperture priority (A)",
            4 => "Shutter priority (S)",
            5 => "Creative",
            6 => "Action",
            7 => "Portrait",
            8 => "Landscape",
            _ => "—"
        };
    }

    private static string ReadMeteringMode(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        ushort? code = ReadExifUShort(exif, ExifTag.MeteringMode);
        if (!code.HasValue && ReadWicRaw(wicMeta, "System.Photo.MeteringMode") is ushort wc)
            code = wc;

        return code switch
        {
            0 => "Unknown",
            1 => "Average",
            2 => "Center weighted average",
            3 => "Spot",
            4 => "Multi-spot",
            5 => "Pattern (Multi-segment)",
            6 => "Partial",
            255 => "Other",
            _ => "—"
        };
    }

    private static string ReadExposureBias(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        double? val = ReadExifDouble(exif, ExifTag.ExposureBiasValue);
        if (!val.HasValue && ReadWicRaw(wicMeta, "System.Photo.ExposureBias") is double wd)
            val = wd;

        if (!val.HasValue)
            return "—";

        if (Math.Abs(val.Value) < 0.005)
            return "0 EV";

        string sign = val.Value > 0 ? "+" : string.Empty;
        return string.Format(CultureInfo.InvariantCulture, "{0}{1:0.##} EV", sign, val.Value);
    }

    private static string ReadFlash(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        ushort? code = ReadExifUShort(exif, ExifTag.Flash);
        if (!code.HasValue && ReadWicRaw(wicMeta, "System.Photo.Flash") is byte wb)
            code = wb;

        if (!code.HasValue)
            return "—";

        int val = code.Value;
        bool fired = (val & 1) != 0;
        int mode = (val >> 3) & 3;
        bool redEye = (val & 0x40) != 0;

        string text = fired ? "Flash fired" : "No flash";
        text += mode switch
        {
            1 => ", compulsory",
            2 => ", suppressed",
            3 => ", auto",
            _ => string.Empty
        };
        if (redEye)
            text += ", red-eye";

        return text;
    }

    private static string ReadFocalLength(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        double? val = ReadExifDouble(exif, ExifTag.FocalLength);
        if (!val.HasValue && ReadWicRaw(wicMeta, "System.Photo.FocalLength") is double wd)
            val = wd;

        if (val.HasValue && val.Value > 0)
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#} mm", val.Value);

        return "—";
    }

    private static string ReadFocalLength35mm(ExifProfile? exif, BitmapMetadata? wicMeta)
    {
        ushort? val = ReadExifUShort(exif, ExifTag.FocalLengthIn35mmFilm);
        if (!val.HasValue)
        {
            object? raw = ReadWicRaw(wicMeta, "System.Photo.FocalLengthInFilm");
            if (raw is ushort u) val = u;
            else if (raw is int i && i > 0) val = (ushort)i;
        }

        return val.HasValue && val.Value > 0 ? $"{val.Value} mm" : "—";
    }

    private static string ReadWhiteBalance(ExifProfile? exif)
    {
        ushort? wb = ReadExifUShort(exif, ExifTag.WhiteBalance);
        return wb switch
        {
            0 => "WB Auto",
            1 => "WB Manual",
            _ => "—"
        };
    }

    private static string ReadColorSpace(ExifProfile? exif)
    {
        ushort? cs = ReadExifUShort(exif, ExifTag.ColorSpace);
        return cs switch
        {
            1 => "sRGB",
            2 => "Adobe RGB",
            65535 => "Uncalibrated",
            _ => "—"
        };
    }

    private static bool TryReadGps(
        ExifProfile? exif,
        BitmapMetadata? wicMeta,
        out double latitude,
        out double longitude,
        out double? altitudeMeters)
    {
        latitude = 0;
        longitude = 0;
        altitudeMeters = null;

        bool gotLat = false;
        bool gotLon = false;

        if (GetRawExif(exif, ExifTag.GPSLatitude) is Rational[] latDms && TryDmsToDecimal(latDms, out double lat))
        {
            string latRef = ReadExifString(exif, ExifTag.GPSLatitudeRef);
            if (latRef.StartsWith("S", StringComparison.OrdinalIgnoreCase))
                lat = -Math.Abs(lat);
            latitude = lat;
            gotLat = true;
        }

        if (GetRawExif(exif, ExifTag.GPSLongitude) is Rational[] lonDms && TryDmsToDecimal(lonDms, out double lon))
        {
            string lonRef = ReadExifString(exif, ExifTag.GPSLongitudeRef);
            if (lonRef.StartsWith("W", StringComparison.OrdinalIgnoreCase))
                lon = -Math.Abs(lon);
            longitude = lon;
            gotLon = true;
        }

        double? alt = ReadExifDouble(exif, ExifTag.GPSAltitude);
        if (alt.HasValue)
        {
            object? altRefRaw = GetRawExif(exif, ExifTag.GPSAltitudeRef);
            bool belowSeaLevel = altRefRaw is byte b && b == 1;
            altitudeMeters = belowSeaLevel ? -alt.Value : alt.Value;
        }

        // WIC fallback (e.g. HEIC / AVIF)
        if ((!gotLat || !gotLon) && wicMeta != null)
        {
            if (ReadWicRaw(wicMeta, "System.GPS.LatitudeDecimal") is double wLat &&
                ReadWicRaw(wicMeta, "System.GPS.LongitudeDecimal") is double wLon)
            {
                latitude = wLat;
                longitude = wLon;
                gotLat = true;
                gotLon = true;
            }
            else if (ReadWicRaw(wicMeta, "System.GPS.Latitude") is ulong[] wLatArr &&
                     ReadWicRaw(wicMeta, "System.GPS.Longitude") is ulong[] wLonArr &&
                     TryWicDmsToDecimal(wLatArr, out double wl) &&
                     TryWicDmsToDecimal(wLonArr, out double wlo))
            {
                string latRef = ReadWicString(wicMeta, "System.GPS.LatitudeRef");
                string lonRef = ReadWicString(wicMeta, "System.GPS.LongitudeRef");
                latitude = latRef.StartsWith("S", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(wl) : wl;
                longitude = lonRef.StartsWith("W", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(wlo) : wlo;
                gotLat = true;
                gotLon = true;
            }
        }

        return gotLat && gotLon && (Math.Abs(latitude) > 0.00001 || Math.Abs(longitude) > 0.00001);
    }

    private static bool TryDmsToDecimal(Rational[] dms, out double decimalDegrees)
    {
        decimalDegrees = 0;
        if (dms.Length < 3)
            return false;

        double deg = dms[0].Denominator != 0 ? dms[0].ToDouble() : 0;
        double min = dms[1].Denominator != 0 ? dms[1].ToDouble() : 0;
        double sec = dms[2].Denominator != 0 ? dms[2].ToDouble() : 0;
        decimalDegrees = deg + (min / 60.0) + (sec / 3600.0);
        return !double.IsNaN(decimalDegrees) && !double.IsInfinity(decimalDegrees);
    }

    private static bool TryWicDmsToDecimal(ulong[] packedDms, out double decimalDegrees)
    {
        decimalDegrees = 0;
        if (packedDms.Length < 3)
            return false;

        double Unpack(ulong packed)
        {
            uint num = (uint)(packed & 0xFFFFFFFF);
            uint den = (uint)(packed >> 32);
            return den != 0 ? num / (double)den : 0;
        }

        decimalDegrees = Unpack(packedDms[0]) + (Unpack(packedDms[1]) / 60.0) + (Unpack(packedDms[2]) / 3600.0);
        return !double.IsNaN(decimalDegrees) && !double.IsInfinity(decimalDegrees);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return "—";
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            int t = a % b;
            a = b;
            b = t;
        }
        return a == 0 ? 1 : a;
    }
}
