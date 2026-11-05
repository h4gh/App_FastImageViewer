using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FastImageViewer.Models;
using FastImageViewer.Services;
using FastImageViewer.Views;
using Microsoft.Win32;

namespace FastImageViewer;

public partial class MainWindow : Window
{
    private const double MinZoomScale = 0.05;
    private const double MaxZoomScale = 32.0;
    private const double ZoomStepFactor = 1.25;

    private readonly ImageCacheService _cacheService = new();
    private readonly ObservableCollection<ImageItem> _items = [];
    private AppSettings _settings;

    private string _currentFolder = string.Empty;
    private int _currentIndex = -1;
    private bool _isOverviewMode;
    private int _imageLoadVersion;
    private CancellationTokenSource? _thumbnailCts;

    // Zoom, Pan & FastStone Magnifier state
    private double _imageBaseWidthDip;
    private double _imageBaseHeightDip;
    private double _fitScale = 1.0;
    private double _currentScale = 1.0;
    private double _panX;
    private double _panY;
    private bool _isFitToWindow = true;
    private bool _isMagnifierActive;
    private bool _isPanning;
    private Point _lastMousePosition;
    private DispatcherTimer? _toastTimer;

    public MainWindow(string? startupPath)
    {
        InitializeComponent();
        ThumbnailListBox.ItemsSource = _items;
        _settings = AppSettingsService.Load();
        UpdateWheelModeButtonText();

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

        _ = LoadThumbnailsOutwardAsync(targetIndex, _thumbnailCts.Token);
    }

    private async Task LoadThumbnailsOutwardAsync(int centerIndex, CancellationToken token)
    {
        var snapshot = _items.ToList();
        int count = snapshot.Count;
        if (count == 0)
            return;

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

        var cached = _cacheService.TryGetCached(filePath);
        if (cached != null)
        {
            SetFullscreenBitmap(cached);
            _cacheService.PrefetchAround(_items, index);
            return;
        }

        var loaded = await _cacheService.GetOrLoadImageAsync(filePath);
        if (loadVersion == _imageLoadVersion && loaded != null)
        {
            SetFullscreenBitmap(loaded);
        }

        _cacheService.PrefetchAround(_items, index);
    }

    private void SetFullscreenBitmap(BitmapSource bitmap)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        // Exact 1:1 physical pixel dimensions in WPF DIPs (ignoring arbitrary EXIF DPI metadata)
        _imageBaseWidthDip = bitmap.PixelWidth / dpiScaleX;
        _imageBaseHeightDip = bitmap.PixelHeight / dpiScaleY;

        FullscreenImage.Width = _imageBaseWidthDip;
        FullscreenImage.Height = _imageBaseHeightDip;
        FullscreenImage.Source = bitmap;

        EndMagnifierOrPan();
        ApplyFitToWindow();
        UpdateStatusText();
    }

    // ==================== ZOOM, PAN & FASTSTONE MAGNIFIER ====================

    private (double Width, double Height) GetViewportSize()
    {
        double w = FullscreenViewGrid.ActualWidth;
        double h = FullscreenViewGrid.ActualHeight;
        if (w <= 0) w = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        if (h <= 0) h = ActualHeight > 0 ? ActualHeight : SystemParameters.PrimaryScreenHeight;
        return (w, h);
    }

    private double CalculateFitScale()
    {
        if (_imageBaseWidthDip <= 0 || _imageBaseHeightDip <= 0)
            return 1.0;

        var (vw, vh) = GetViewportSize();
        if (vw <= 0 || vh <= 0)
            return 1.0;

        double scaleX = vw / _imageBaseWidthDip;
        double scaleY = vh / _imageBaseHeightDip;
        return Math.Min(1.0, Math.Min(scaleX, scaleY));
    }

    private void ApplyFitToWindow()
    {
        _fitScale = CalculateFitScale();
        _currentScale = _fitScale;
        _isFitToWindow = true;

        var (vw, vh) = GetViewportSize();
        _panX = (vw - (_imageBaseWidthDip * _currentScale)) / 2.0;
        _panY = (vh - (_imageBaseHeightDip * _currentScale)) / 2.0;

        UpdateImageTransform();
    }

    private void ZoomAtPoint(double newScale, Point pivotInViewport)
    {
        if (_imageBaseWidthDip <= 0 || _imageBaseHeightDip <= 0)
            return;

        _fitScale = CalculateFitScale();
        newScale = Math.Clamp(newScale, MinZoomScale, MaxZoomScale);

        // Compute image-local coordinate (u, v) currently under pivotInViewport
        double u = (pivotInViewport.X - _panX) / _currentScale;
        double v = (pivotInViewport.Y - _panY) / _currentScale;

        u = Math.Clamp(u, 0, _imageBaseWidthDip);
        v = Math.Clamp(v, 0, _imageBaseHeightDip);

        _currentScale = newScale;
        _isFitToWindow = false;

        _panX = pivotInViewport.X - (u * _currentScale);
        _panY = pivotInViewport.Y - (v * _currentScale);

        ClampPanToViewport();
        UpdateImageTransform();
    }

    private void ToggleOneToOneZoom(Point pivotInViewport)
    {
        if (FullscreenImage.Source == null)
            return;

        _fitScale = CalculateFitScale();

        // If already at 100% (1:1) and not in Fit-to-Window (or if image fits 1:1, toggle between 1:1 and 200%)
        if (!_isFitToWindow && Math.Abs(_currentScale - 1.0) < 0.001)
        {
            ApplyFitToWindow();
            return;
        }

        if (_isFitToWindow && Math.Abs(_fitScale - 1.0) < 0.001)
        {
            // Image is already smaller than screen (displayed at 100%); '*' zooms to 200% or resets
            ZoomAtPoint(2.0, pivotInViewport);
            return;
        }

        ZoomAtPoint(1.0, pivotInViewport);
    }

    private void ClampPanToViewport()
    {
        var (vw, vh) = GetViewportSize();
        double scaledW = _imageBaseWidthDip * _currentScale;
        double scaledH = _imageBaseHeightDip * _currentScale;

        if (scaledW <= vw)
        {
            _panX = (vw - scaledW) / 2.0;
        }
        else
        {
            _panX = Math.Clamp(_panX, vw - scaledW, 0.0);
        }

        if (scaledH <= vh)
        {
            _panY = (vh - scaledH) / 2.0;
        }
        else
        {
            _panY = Math.Clamp(_panY, vh - scaledH, 0.0);
        }
    }

    private void UpdateImageTransform()
    {
        // Use NearestNeighbor when zoomed >= 200% so individual pixels are crisp, HighQuality otherwise
        RenderOptions.SetBitmapScalingMode(
            FullscreenImage,
            _currentScale >= 2.0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        ImageScaleTransform.ScaleX = _currentScale;
        ImageScaleTransform.ScaleY = _currentScale;
        ImageTranslateTransform.X = _panX;
        ImageTranslateTransform.Y = _panY;
    }

    private void EndMagnifierOrPan()
    {
        if (_isMagnifierActive || _isPanning)
        {
            _isMagnifierActive = false;
            _isPanning = false;
            FullscreenViewGrid.ReleaseMouseCapture();
            FullscreenViewGrid.Cursor = Cursors.Arrow;
        }
    }

    private Point GetZoomPivotPoint()
    {
        if (FullscreenViewGrid.IsMouseOver)
        {
            return Mouse.GetPosition(FullscreenViewGrid);
        }

        var (vw, vh) = GetViewportSize();
        return new Point(vw / 2.0, vh / 2.0);
    }

    private void FullscreenViewGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FullscreenImage.Source == null)
            return;

        if (_isFitToWindow)
        {
            ApplyFitToWindow();
        }
        else
        {
            _fitScale = CalculateFitScale();
            ClampPanToViewport();
            UpdateImageTransform();
        }
    }

    private void FullscreenViewGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FullscreenImage.Source == null)
            return;

        if (e.ClickCount == 2)
        {
            e.Handled = true;
            EndMagnifierOrPan();
            ApplyFitToWindow();
            SwitchMode(overviewMode: true);
            return;
        }

        if (e.ClickCount == 1)
        {
            e.Handled = true;
            Point pos = e.GetPosition(FullscreenViewGrid);
            _lastMousePosition = pos;

            if (_isFitToWindow)
            {
                // FastStone Lupa: hold Left Mouse Button -> instant 100% (1:1) at cursor + drag to pan
                _isMagnifierActive = true;
                double targetMagScale = _fitScale < 1.0 ? 1.0 : 2.0;
                ZoomAtPoint(targetMagScale, pos);
                _isFitToWindow = false;
                FullscreenViewGrid.Cursor = Cursors.SizeAll;
                FullscreenViewGrid.CaptureMouse();
            }
            else
            {
                // Already zoomed in -> drag to pan
                _isPanning = true;
                FullscreenViewGrid.Cursor = Cursors.SizeAll;
                FullscreenViewGrid.CaptureMouse();
            }
        }
    }

    private void FullscreenViewGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMagnifierActive && !_isPanning)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_isMagnifierActive)
            {
                EndMagnifierOrPan();
                ApplyFitToWindow();
            }
            else
            {
                EndMagnifierOrPan();
            }
            return;
        }

        Point currentPos = e.GetPosition(FullscreenViewGrid);
        double dx = currentPos.X - _lastMousePosition.X;
        double dy = currentPos.Y - _lastMousePosition.Y;
        _lastMousePosition = currentPos;

        // In FastStone Magnifier, amplify mouse movement slightly (1.5x) so you can easily inspect edges
        double panMultiplier = _isMagnifierActive ? 1.5 : 1.0;
        _panX += dx * panMultiplier;
        _panY += dy * panMultiplier;

        ClampPanToViewport();
        UpdateImageTransform();
    }

    private void FullscreenViewGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isMagnifierActive)
        {
            e.Handled = true;
            EndMagnifierOrPan();
            ApplyFitToWindow();
        }
        else if (_isPanning)
        {
            e.Handled = true;
            EndMagnifierOrPan();
        }
    }

    private void FullscreenViewGrid_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FullscreenImage.Source == null || _items.Count == 0)
            return;

        e.Handled = true;
        bool shouldZoom = _settings.MouseWheelZoom || (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (!shouldZoom)
        {
            if (e.Delta < 0 && _currentIndex < _items.Count - 1)
            {
                SetCurrentIndex(_currentIndex + 1);
            }
            else if (e.Delta > 0 && _currentIndex > 0)
            {
                SetCurrentIndex(_currentIndex - 1);
            }
            return;
        }

        Point cursor = e.GetPosition(FullscreenViewGrid);
        _fitScale = CalculateFitScale();

        if (e.Delta > 0)
        {
            double nextScale = _currentScale * ZoomStepFactor;
            ZoomAtPoint(nextScale, cursor);
        }
        else if (e.Delta < 0)
        {
            double nextScale = _currentScale / ZoomStepFactor;
            if (nextScale <= _fitScale * 1.02)
            {
                ApplyFitToWindow();
            }
            else
            {
                ZoomAtPoint(nextScale, cursor);
            }
        }
    }

    private void ToggleMouseWheelMode()
    {
        _settings.MouseWheelZoom = !_settings.MouseWheelZoom;
        AppSettingsService.Save(_settings);
        UpdateWheelModeButtonText();

        string modeLabel = _settings.MouseWheelZoom
            ? "Kolečko myši: Zoom na kurzor"
            : "Kolečko myši: Listování fotkami (Ctrl+Kolečko = Zoom)";
        ShowToast(modeLabel);
    }

    private void UpdateWheelModeButtonText()
    {
        WheelModeButton.Content = _settings.MouseWheelZoom
            ? "Kolečko: Zoom (M)"
            : "Kolečko: Listování (M)";
    }

    private void WheelModeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMouseWheelMode();
    }

    // ==================== FILE OPERATIONS (COPY & DELETE TO RECYCLE BIN) ====================

    private void ShowCopyFileDialog()
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count)
            return;

        EndMagnifierOrPan();
        string currentFilePath = _items[_currentIndex].FilePath;
        string initialTarget = !string.IsNullOrWhiteSpace(_settings.LastCopyTargetFolder)
            ? _settings.LastCopyTargetFolder!
            : _currentFolder;

        var dlg = new CopyFileWindow(currentFilePath, initialTarget)
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.SelectedTargetFolder))
        {
            _settings.LastCopyTargetFolder = dlg.SelectedTargetFolder;
            AppSettingsService.Save(_settings);
            ShowToast($"Zkopírováno do: {dlg.SelectedTargetFolder}");
        }
    }

    private void DeleteCurrentFileToRecycleBin()
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count)
            return;

        EndMagnifierOrPan();
        int indexToDelete = _currentIndex;
        string filePath = _items[indexToDelete].FilePath;

        if (!FileOperationsService.MoveToRecycleBin(filePath))
            return;

        _cacheService.Invalidate(filePath);
        _items.RemoveAt(indexToDelete);

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

        int nextIndex = Math.Min(indexToDelete, _items.Count - 1);
        SetCurrentIndex(nextIndex);

        if (_isOverviewMode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentIndex >= 0 && _currentIndex < _items.Count &&
                    ThumbnailListBox.ItemContainerGenerator.ContainerFromIndex(_currentIndex) is ListBoxItem container)
                {
                    container.Focus();
                }
            }), DispatcherPriority.Loaded);
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;

        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.8)
            };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                ToastBorder.Visibility = Visibility.Collapsed;
            };
        }

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ==================== OVERVIEW & KEYBOARD NAVIGATION ====================

    private void SwitchMode(bool overviewMode)
    {
        EndMagnifierOrPan();
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
            FullscreenViewGrid.UpdateLayout();
            ApplyFitToWindow();
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

        string resolutionInfo = string.Empty;
        if (FullscreenImage.Source is BitmapSource bmp)
        {
            resolutionInfo = $"   •   {bmp.PixelWidth} × {bmp.PixelHeight} px";
        }

        StatusCounterText.Text = $"{_currentIndex + 1} / {_items.Count}   —   {current.FileName}{resolutionInfo}{sizeInfo}";
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

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            ShowCopyFileDialog();
            return;
        }

        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            ToggleMouseWheelMode();
            return;
        }

        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            DeleteCurrentFileToRecycleBin();
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
            // Zoom shortcuts on Numpad or standard keys
            switch (e.Key)
            {
                case Key.Add:
                    e.Handled = true;
                    ZoomAtPoint(_currentScale * ZoomStepFactor, GetZoomPivotPoint());
                    return;

                case Key.Subtract:
                    e.Handled = true;
                    HandleZoomOutKey();
                    return;

                case Key.Multiply:
                    e.Handled = true;
                    ToggleOneToOneZoom(GetZoomPivotPoint());
                    return;

                case Key.Divide:
                case Key.NumPad0:
                    e.Handled = true;
                    ApplyFitToWindow();
                    return;

                case Key.Right:
                case Key.Down:
                case Key.Space:
                case Key.PageDown:
                    e.Handled = true;
                    if (_currentIndex < _items.Count - 1)
                        SetCurrentIndex(_currentIndex + 1);
                    return;

                case Key.Left:
                case Key.Up:
                case Key.Back:
                case Key.PageUp:
                    e.Handled = true;
                    if (_currentIndex > 0)
                        SetCurrentIndex(_currentIndex - 1);
                    return;

                case Key.Home:
                    e.Handled = true;
                    if (_items.Count > 0)
                        SetCurrentIndex(0);
                    return;

                case Key.End:
                    e.Handled = true;
                    if (_items.Count > 0)
                        SetCurrentIndex(_items.Count - 1);
                    return;
            }
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_isOverviewMode || FullscreenImage.Source == null || string.IsNullOrEmpty(e.Text))
            return;

        // Handles '+', '-', '*', '/' across all keyboard layouts (including Czech QWERTZ and English QWERTY)
        switch (e.Text)
        {
            case "+":
                e.Handled = true;
                ZoomAtPoint(_currentScale * ZoomStepFactor, GetZoomPivotPoint());
                break;

            case "-":
                e.Handled = true;
                HandleZoomOutKey();
                break;

            case "*":
                e.Handled = true;
                ToggleOneToOneZoom(GetZoomPivotPoint());
                break;

            case "/":
            case "0":
                e.Handled = true;
                ApplyFitToWindow();
                break;
        }
    }

    private void HandleZoomOutKey()
    {
        _fitScale = CalculateFitScale();
        double nextScale = _currentScale / ZoomStepFactor;
        if (!_isFitToWindow && _currentScale > _fitScale && nextScale <= _fitScale * 1.02)
        {
            ApplyFitToWindow();
        }
        else
        {
            ZoomAtPoint(nextScale, GetZoomPivotPoint());
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
