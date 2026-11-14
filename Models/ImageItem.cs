using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace FastImageViewer.Models;

public sealed class ImageItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _isLoadingThumbnail;
    private int _displayIndex;
    private string _dimensionsText = "—";

    public ImageItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        ExtensionBadge = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Exists)
            {
                FileSizeBytes = fi.Length;
                LastWriteTimeUtc = fi.LastWriteTimeUtc;
            }
            else
            {
                LastWriteTimeUtc = DateTime.MinValue;
            }
        }
        catch
        {
            FileSizeBytes = 0;
            LastWriteTimeUtc = DateTime.MinValue;
        }
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string ExtensionBadge { get; }
    public long FileSizeBytes { get; }
    public DateTime LastWriteTimeUtc { get; }

    public int DisplayIndex
    {
        get => _displayIndex;
        set
        {
            if (_displayIndex != value)
            {
                _displayIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IndexBadgeText));
            }
        }
    }

    public string IndexBadgeText => $"#{_displayIndex}";

    public string DimensionsText
    {
        get => _dimensionsText;
        set
        {
            if (_dimensionsText != value)
            {
                _dimensionsText = value;
                OnPropertyChanged();
            }
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsLoadingThumbnail
    {
        get => _isLoadingThumbnail;
        set => _isLoadingThumbnail = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
