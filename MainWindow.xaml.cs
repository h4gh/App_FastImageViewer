using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastImageViewer.Models;
using FastImageViewer.Services;
using Microsoft.Win32;

namespace FastImageViewer;

public partial class MainWindow : Window
{
    private readonly ImageCacheService _cacheService = new();
    private readonly ObservableCollection<ImageItem> _items = [];
    private AppSettings _settings;

    private string _currentFolder = string.Empty;
    private int _currentIndex = -1;
    private bool _isOverviewMode;
    private int _imageLoadVersion;
    private CancellationTokenSource? _thumbnailCts;

    public MainWindow(string? startupPath)
    {
        InitializeComponent();
        ThumbnailListBox.ItemsSource = _items;
        _settings = AppSettingsService.Load();

        Loaded += (_, _) => InitializeFromPath(startupPath);
    }

    private void InitializeFromPath(string? startupPath)
    {
        if (!string.IsNullOrWhiteSpace(startupPath))
        {
            string cleaned = startupPath.Trim('"');
            if (File.Exists(cleaned) && ImageCacheService.IsSupportedFile(cleaned))
            {
                OpenImageFile(cleaned, startInFullscreen: true);
                return;
            }
            if (Directory.Exists(cleaned))
            {
                LoadFolder(cleaned, selectedFilePath: null, startInFullscreen: false);
                return;
            }
        }

        // Fallback when launched without arguments: last used folder -> MyPictures
        string fallbackFolder = _settings.LastFolderPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fallbackFolder) || !Directory.Exists(fallbackFolder))
        {
            fallbackFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        LoadFolder(fallbackFolder, selectedFilePath: null, startInFullscreen: false);
    }

    private void OpenImageFile(string filePath, bool startInFullscreen)
    {
        string fullPath = Path.GetFullPath(filePath);
        string? folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(folder))
            return;

        LoadFolder(folder, fullPath, startInFullscreen);
    }

    private void LoadFolder(string folderPath, string? selectedFilePath, bool startInFullscreen)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();

        _currentFolder = Path.GetFullPath(folderPath);
        _settings.LastFolderPath = _currentFolder;
        AppSettingsService.Save(_settings);

        FolderPathText.Text = _currentFolder;
        _cacheService.Clear();
        _items.Clear();

        List<string> files = ImageCacheService.GetImagesInFolder(_currentFolder);
        int targetIndex = 0;

        for (int i = 0; i < files.Count; i++)
        {
            _items.Add(new ImageItem(files[i]));
            if (selectedFilePath != null && string.Equals(files[i], selectedFilePath, StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = i;
            }
        }

        if (_items.Count == 0)
        {
            _currentIndex = -1;
            FullscreenImage.Source = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            ThumbnailListBox.Visibility = Visibility.Collapsed;
            UpdateStatusText();
            SwitchMode(overviewMode: true);
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        ThumbnailListBox.Visibility = Visibility.Visible;

        SetCurrentIndex(targetIndex);
        SwitchMode(overviewMode: !startInFullscreen);

        // Start loading thumbnails in background outward from targetIndex
        _ = LoadThumbnailsOutwardAsync(targetIndex, _thumbnailCts.Token);
    }

    private async Task LoadThumbnailsOutwardAsync(int centerIndex, CancellationToken token)
    {
        var snapshot = _items.ToList();
        int count = snapshot.Count;
        if (count == 0)
            return;

        // Generate indices spiraling outward from centerIndex (center, +1, -1, +2, -2, ...)
        var orderedIndices = new List<int>(count);
        if (centerIndex >= 0 && centerIndex < count)
            orderedIndices.Add(centerIndex);

        for (int step = 1; orderedIndices.Count < count; step++)
        {
            int right = centerIndex + step;
            int left = centerIndex - step;
            if (right < count) orderedIndices.Add(right);
            if (left >= 0) orderedIndices.Add(left);
            if (right >= count && left < 0) break;
        }

        var tasks = new List<Task>(Math.Min(count, 8));
        foreach (int idx in orderedIndices)
        {
            if (token.IsCancellationRequested)
                return;

            tasks.Add(_cacheService.EnsureThumbnailLoadedAsync(snapshot[idx], token));
            if (tasks.Count >= 6)
            {
                await Task.WhenAny(tasks);
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }

        await Task.WhenAll(tasks);
    }

    private void SetCurrentIndex(int index)
    {
        if (_items.Count == 0)
        {
            _currentIndex = -1;
            FullscreenImage.Source = null;
            UpdateStatusText();
            return;
        }

        int clamped = Math.Clamp(index, 0, _items.Count - 1);
        _currentIndex = clamped;

        if (ThumbnailListBox.SelectedIndex != _currentIndex)
        {
            ThumbnailListBox.SelectedIndex = _currentIndex;
        }

        UpdateStatusText();
        _ = DisplayCurrentImageAsync(_currentIndex);
    }

    private async Task DisplayCurrentImageAsync(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;

        int loadVersion = ++_imageLoadVersion;
        string filePath = _items[index].FilePath;

        // 1. Check synchronous in-memory cache first for instant 0ms switch
        var cached = _cacheService.TryGetCached(filePath);
        if (cached != null)
        {
            FullscreenImage.Source = cached;
            _cacheService.PrefetchAround(_items, index);
            return;
        }

        // 2. Otherwise load asynchronously and prefetch neighbors
        var loaded = await _cacheService.GetOrLoadImageAsync(filePath);
        if (loadVersion == _imageLoadVersion && loaded != null)
        {
            FullscreenImage.Source = loaded;
        }

        _cacheService.PrefetchAround(_items, index);
    }

    private void SwitchMode(bool overviewMode)
    {
        _isOverviewMode = overviewMode;

        if (_isOverviewMode)
        {
            FullscreenViewGrid.Visibility = Visibility.Collapsed;
            OverviewGrid.Visibility = Visibility.Visible;
            OverviewGrid.UpdateLayout();

            if (_currentIndex >= 0 && _currentIndex < _items.Count)
            {
                ThumbnailListBox.SelectedIndex = _currentIndex;
                var selectedItem = _items[_currentIndex];
                ThumbnailListBox.ScrollIntoView(selectedItem);
                ThumbnailListBox.UpdateLayout();

                if (ThumbnailListBox.ItemContainerGenerator.ContainerFromIndex(_currentIndex) is ListBoxItem container)
                {
                    container.Focus();
                }
                else
                {
                    ThumbnailListBox.Focus();
                }
            }
            else
            {
                ThumbnailListBox.Focus();
            }
        }
        else
        {
            if (_items.Count == 0)
                return;

            OverviewGrid.Visibility = Visibility.Collapsed;
            FullscreenViewGrid.Visibility = Visibility.Visible;
            Focus();
        }
    }

    private void UpdateStatusText()
    {
        if (_items.Count == 0 || _currentIndex < 0 || _currentIndex >= _items.Count)
        {
            StatusCounterText.Text = "0 obrázků";
            return;
        }

        var current = _items[_currentIndex];
        string sizeInfo = string.Empty;
        try
        {
            var fi = new FileInfo(current.FilePath);
            if (fi.Exists)
            {
                double kb = fi.Length / 1024.0;
                sizeInfo = kb >= 1024.0 ? $"   ({kb / 1024.0:F2} MB)" : $"   ({kb:F0} KB)";
            }
        }
        catch
        {
            // Ignore file info errors
        }

        StatusCounterText.Text = $"{_currentIndex + 1} / {_items.Count}   —   {current.FileName}{sizeInfo}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.O && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control))
        {
            e.Handled = true;
            ShowOpenFileDialog();
            return;
        }

        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            e.Handled = true;
            if (_items.Count > 0)
            {
                SwitchMode(!_isOverviewMode);
            }
            else
            {
                ShowOpenFileDialog();
            }
            return;
        }

        if (!_isOverviewMode)
        {
            // Fullscreen navigation (FastStone style)
            switch (e.Key)
            {
                case Key.Right:
                case Key.Down:
                case Key.Space:
                case Key.PageDown:
                    e.Handled = true;
                    if (_currentIndex < _items.Count - 1)
                        SetCurrentIndex(_currentIndex + 1);
                    break;

                case Key.Left:
                case Key.Up:
                case Key.Back:
                case Key.PageUp:
                    e.Handled = true;
                    if (_currentIndex > 0)
                        SetCurrentIndex(_currentIndex - 1);
                    break;

                case Key.Home:
                    e.Handled = true;
                    if (_items.Count > 0)
                        SetCurrentIndex(0);
                    break;

                case Key.End:
                    e.Handled = true;
                    if (_items.Count > 0)
                        SetCurrentIndex(_items.Count - 1);
                    break;
            }
        }
    }

    private void FullscreenViewGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            SwitchMode(overviewMode: true);
        }
    }

    private void FullscreenViewGrid_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_items.Count == 0)
            return;

        if (e.Delta < 0 && _currentIndex < _items.Count - 1)
        {
            SetCurrentIndex(_currentIndex + 1);
        }
        else if (e.Delta > 0 && _currentIndex > 0)
        {
            SetCurrentIndex(_currentIndex - 1);
        }
    }

    private void ThumbnailListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailListBox.SelectedIndex >= 0 && ThumbnailListBox.SelectedIndex < _items.Count)
        {
            int newIdx = ThumbnailListBox.SelectedIndex;
            if (newIdx != _currentIndex)
            {
                SetCurrentIndex(newIdx);
            }
        }
    }

    private void ThumbnailItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is ImageItem imageItem)
        {
            int index = _items.IndexOf(imageItem);
            if (index >= 0)
            {
                SetCurrentIndex(index);
                SwitchMode(overviewMode: false);
                e.Handled = true;
            }
        }
    }

    private void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        ShowOpenFileDialog();
    }

    private void ShowOpenFileDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Vyberte obrázek k prohlížení",
            Filter = "Obrázky (*.jpg;*.jpeg;*.png;*.webp;*.gif)|*.jpg;*.jpeg;*.png;*.webp;*.gif|Všechny soubory (*.*)|*.*",
            InitialDirectory = Directory.Exists(_currentFolder)
                ? _currentFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dlg.ShowDialog(this) == true)
        {
            OpenImageFile(dlg.FileName, startInFullscreen: true);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            string first = files[0];
            if (File.Exists(first) && ImageCacheService.IsSupportedFile(first))
            {
                OpenImageFile(first, startInFullscreen: !_isOverviewMode);
            }
            else if (Directory.Exists(first))
            {
                LoadFolder(first, selectedFilePath: null, startInFullscreen: false);
            }
        }
    }
}
