using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace FastImageViewer.Models;

public sealed class ImageItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _isLoadingThumbnail;

    public ImageItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }

    public string FilePath { get; }
    public string FileName { get; }

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
