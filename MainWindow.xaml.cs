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
    private readonly ObservableCollection<FolderNode> _rootFolderNodes = [];
    private bool _isSyncingFolderTree;
    private bool _isSyncingSortCombo;
    private bool _isSyncingFilmstripSizeCombo;
    private bool _isSortingItems;
    private static readonly int[] FilmstripScaleSteps = [75, 90, 100, 115, 125, 150, 175, 200];
    private long _totalFolderBytes;
    private AppSettings _settings;
    private PhotoDetailedMetadata? _currentMetadata;

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

    // Crop Mode state (Phase 3)
    private enum CropDragAction
    {
        None,
        CreateNew,
        Move,
        ResizeNW,
        ResizeN,
        ResizeNE,
        ResizeE,
        ResizeSE,
        ResizeS,
        ResizeSW,
        ResizeW
    }

    private bool _isCropMode;
    private bool _hasCropSelection;
    private CropDragAction _cropDragAction = CropDragAction.None;
    private Rect _cropRect;
    private Point _cropDragStartMouse;
    private Rect _cropDragStartRect;

    // Pending in-memory rotation/flip state
    private bool _hasUnsavedTransform;
    private BitmapSource? _untransformedOriginalBitmap;
    private bool _isResolvingTransform;

    public MainWindow(string? startupPath)
    {
        InitializeComponent();
        ThumbnailListBox.ItemsSource = _items;
        FilmstripListBox.ItemsSource = _items;
        FolderTreeView.ItemsSource = _rootFolderNodes;
        _settings = AppSettingsService.Load();

        var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string versionStr = asmVersion != null ? $"v{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}" : "v1.2.0";
        AppVersionText.Text = versionStr;
        Title = $"FastImageViewer {versionStr}";

        if (!string.Equals(_settings.LastRunVersion, versionStr, StringComparison.Ordinal))
        {
            _settings.LastRunVersion = versionStr;
            AppSettingsService.Save(_settings);
            FileOperationsService.RefreshShellIconCache();
        }

        OverviewLeftColumn.Width = new GridLength(Math.Clamp(_settings.OverviewLeftPaneWidth, 180, 600));
        OverviewPreviewRow.Height = new GridLength(Math.Clamp(_settings.OverviewPreviewPaneHeight, 100, 500));

        int sortIdx = Math.Clamp((int)_settings.SortMode, 0, 4);
        _isSyncingSortCombo = true;
        FilmstripSortComboBox.SelectedIndex = sortIdx;
        OverviewSortComboBox.SelectedIndex = sortIdx;
        _isSyncingSortCombo = false;

        UpdateFilmstripPinButtonUI();
        UpdateRightExifPinButtonUI();
        ApplyFilmstripScale(_settings.FilmstripScalePercent, saveSetting: false);
        UpdateWheelModeButtonText();
        UpdateEditSaveModeButtonText();
        InitializeFolderTree();

        Loaded += (_, _) => InitializeFromPath(startupPath);
        Closing += MainWindow_Closing;
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

    private void LoadFolder(string folderPath, string? selectedFilePath, bool startInFullscreen, bool syncTree = true, bool keepTreeFocus = false)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();

        ExitCropMode();
        _currentFolder = Path.GetFullPath(folderPath);
        _settings.LastFolderPath = _currentFolder;
        AppSettingsService.Save(_settings);

        FolderPathText.Text = _currentFolder;
        FilmstripFolderPathText.Text = $"📁 {_currentFolder}";
        if (syncTree)
        {
            SyncFolderTreeToPath(_currentFolder);
        }

        _cacheService.Clear();
        _isSortingItems = true;
        try
        {
            _items.Clear();

            List<string> files = ImageCacheService.GetImagesInFolder(_currentFolder);
            List<ImageItem> loadedItems = files.Select(f => new ImageItem(f)).ToList();
            SortItemsList(loadedItems, _settings.SortMode);

            int targetIndex = 0;
            for (int i = 0; i < loadedItems.Count; i++)
            {
                _items.Add(loadedItems[i]);
                if (selectedFilePath != null && string.Equals(loadedItems[i].FilePath, selectedFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                }
            }
            RefreshItemIndicesAndFolderSummary();

            if (_items.Count == 0)
            {
                _currentIndex = -1;
                FullscreenImage.Source = null;
                OverviewPreviewImage.Source = null;
                OverviewPreviewMetaText.Text = string.Empty;
                TopFilmstripPanel.Visibility = Visibility.Collapsed;
                RightExifPanel.Visibility = Visibility.Collapsed;
                EmptyStatePanel.Visibility = Visibility.Visible;
                ThumbnailListBox.Visibility = Visibility.Collapsed;
                UpdateFilmstripNavigationButtons();
                UpdateStatusText();
                SwitchMode(overviewMode: true, keepTreeFocus: keepTreeFocus);
                return;
            }

            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ThumbnailListBox.Visibility = Visibility.Visible;

            _currentIndex = -1;
            SetCurrentIndex(targetIndex);
            SwitchMode(overviewMode: !startInFullscreen, keepTreeFocus: keepTreeFocus);

            _ = LoadThumbnailsOutwardAsync(targetIndex, _thumbnailCts.Token);
        }
        finally
        {
            _isSortingItems = false;
        }
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
        ExitCropMode();

        if (_items.Count == 0)
        {
            _currentIndex = -1;
            FullscreenImage.Source = null;
            OverviewPreviewImage.Source = null;
            OverviewPreviewMetaText.Text = string.Empty;
            UpdateFilmstripNavigationButtons();
            UpdateStatusText();
            return;
        }

        int clamped = Math.Clamp(index, 0, _items.Count - 1);
        if (clamped != _currentIndex && !ResolvePendingTransformBeforeLeave())
        {
            if (ThumbnailListBox.SelectedIndex != _currentIndex)
            {
                ThumbnailListBox.SelectedIndex = _currentIndex;
            }
            if (FilmstripListBox.SelectedIndex != _currentIndex)
            {
                FilmstripListBox.SelectedIndex = _currentIndex;
            }
            return;
        }

        // Re-clamp in case a new _rotated file was inserted into _items
        clamped = Math.Clamp(index, 0, _items.Count - 1);
        _currentIndex = clamped;

        if (ThumbnailListBox.SelectedIndex != _currentIndex)
        {
            ThumbnailListBox.SelectedIndex = _currentIndex;
        }
        if (FilmstripListBox.SelectedIndex != _currentIndex)
        {
            FilmstripListBox.SelectedIndex = _currentIndex;
        }

        UpdateFilmstripNavigationButtons();
        if (TopFilmstripPanel.Visibility == Visibility.Visible)
        {
            ScrollFilmstripToCurrentItem();
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

        _imageBaseWidthDip = bitmap.PixelWidth / dpiScaleX;
        _imageBaseHeightDip = bitmap.PixelHeight / dpiScaleY;

        FullscreenImage.Width = _imageBaseWidthDip;
        FullscreenImage.Height = _imageBaseHeightDip;
        FullscreenImage.Source = bitmap;
        OverviewPreviewImage.Source = bitmap;

        if (_currentIndex >= 0 && _currentIndex < _items.Count)
        {
            _items[_currentIndex].DimensionsText = $"{bitmap.PixelWidth}×{bitmap.PixelHeight}";
        }

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

        if (!_isFitToWindow && Math.Abs(_currentScale - 1.0) < 0.001)
        {
            ApplyFitToWindow();
            return;
        }

        if (_isFitToWindow && Math.Abs(_fitScale - 1.0) < 0.001)
        {
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

        if (_isCropMode)
        {
            UpdateCropOverlayVisuals();
        }
    }

    private void FullscreenViewGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isCropMode || FullscreenImage.Source == null)
            return;

        if (e.OriginalSource is DependencyObject dep &&
            (IsDescendantOf(dep, LeftFlyoutPanel) || IsDescendantOf(dep, RightExifPanel) || IsDescendantOf(dep, TopFilmstripPanel)))
            return;

        LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        if (!_settings.PinRightExifPanel)
        {
            RightExifPanel.Visibility = Visibility.Collapsed;
        }
        if (!_settings.PinTopFilmstrip)
        {
            TopFilmstripPanel.Visibility = Visibility.Collapsed;
        }

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
                _isMagnifierActive = true;
                double targetMagScale = _fitScale < 1.0 ? 1.0 : 2.0;
                ZoomAtPoint(targetMagScale, pos);
                _isFitToWindow = false;
                FullscreenViewGrid.Cursor = Cursors.SizeAll;
                FullscreenViewGrid.CaptureMouse();
            }
            else
            {
                _isPanning = true;
                FullscreenViewGrid.Cursor = Cursors.SizeAll;
                FullscreenViewGrid.CaptureMouse();
            }
        }
    }

    private void FullscreenViewGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isCropMode)
            return;

        Point currentPos = e.GetPosition(FullscreenViewGrid);

        if (!_isMagnifierActive && !_isPanning)
        {
            var (vw, _) = GetViewportSize();

            // Show TopFilmstripPanel when mouse approaches the top edge of the screen (<= 14 px)
            if (currentPos.Y <= 14)
            {
                if (TopFilmstripPanel.Visibility != Visibility.Visible)
                {
                    TopFilmstripPanel.Visibility = Visibility.Visible;
                    ScrollFilmstripToCurrentItem();
                }
            }
            else if (TopFilmstripPanel.Visibility == Visibility.Visible && !_settings.PinTopFilmstrip)
            {
                if (!TopFilmstripPanel.IsMouseOver &&
                    !FilmstripSortComboBox.IsDropDownOpen &&
                    !FilmstripSizeComboBox.IsDropDownOpen &&
                    currentPos.Y > TopFilmstripPanel.ActualHeight + 14)
                {
                    TopFilmstripPanel.Visibility = Visibility.Collapsed;
                }
            }

            // Show LeftFlyoutPanel when mouse approaches the left edge of the screen (<= 14 px)
            if (currentPos.X <= 14 && (TopFilmstripPanel.Visibility != Visibility.Visible || currentPos.Y > TopFilmstripPanel.ActualHeight))
            {
                if (LeftFlyoutPanel.Visibility != Visibility.Visible)
                {
                    LeftFlyoutPanel.Visibility = Visibility.Visible;
                }
            }
            else if (LeftFlyoutPanel.Visibility == Visibility.Visible)
            {
                if (!LeftFlyoutPanel.IsMouseOver && currentPos.X > LeftFlyoutPanel.ActualWidth + 14)
                {
                    LeftFlyoutPanel.Visibility = Visibility.Collapsed;
                }
            }

            // Show RightExifPanel when mouse approaches the right edge of the screen (>= vw - 14)
            if (currentPos.X >= vw - 14 && (TopFilmstripPanel.Visibility != Visibility.Visible || currentPos.Y > TopFilmstripPanel.ActualHeight))
            {
                if (RightExifPanel.Visibility != Visibility.Visible)
                {
                    RightExifPanel.Visibility = Visibility.Visible;
                    UpdateRightExifPanel();
                }
            }
            else if (RightExifPanel.Visibility == Visibility.Visible && !_settings.PinRightExifPanel)
            {
                if (!RightExifPanel.IsMouseOver && currentPos.X < vw - RightExifPanel.ActualWidth - 14)
                {
                    RightExifPanel.Visibility = Visibility.Collapsed;
                }
            }
            return;
        }

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

        double dx = currentPos.X - _lastMousePosition.X;
        double dy = currentPos.Y - _lastMousePosition.Y;
        _lastMousePosition = currentPos;

        double panMultiplier = _isMagnifierActive ? 1.5 : 1.0;
        _panX += dx * panMultiplier;
        _panY += dy * panMultiplier;

        ClampPanToViewport();
        UpdateImageTransform();
    }

    private void FullscreenViewGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isCropMode)
            return;

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
        if (_isCropMode || FullscreenImage.Source == null || _items.Count == 0)
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

        if (FlyoutWheelModeButton != null)
        {
            FlyoutWheelModeButton.Content = _settings.MouseWheelZoom
                ? "🖱 Kolečko: Zoom"
                : "🖱 Kolečko: Listování";
        }
    }

    private void WheelModeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMouseWheelMode();
    }

    private void ToggleEditSaveMode()
    {
        _settings.UseSaveAsDialogForEdits = !_settings.UseSaveAsDialogForEdits;
        AppSettingsService.Save(_settings);
        UpdateEditSaveModeButtonText();

        string label = _settings.UseSaveAsDialogForEdits
            ? "Režim ukládání úprav: Dialog „Uložit jako…“"
            : "Režim ukládání úprav: Automatický sufix (_rotated / _croped)";
        ShowToast(label);
    }

    private void UpdateEditSaveModeButtonText()
    {
        if (EditSaveModeButton != null)
        {
            EditSaveModeButton.Content = _settings.UseSaveAsDialogForEdits
                ? "Ukládání: Uložit jako… (U)"
                : "Ukládání: Auto sufix (U)";
        }

        if (FlyoutEditSaveModeButton != null)
        {
            FlyoutEditSaveModeButton.Content = _settings.UseSaveAsDialogForEdits
                ? "💾 Ukládání: Uložit jako…"
                : "💾 Ukládání: Auto sufix";
        }
    }

    private void EditSaveModeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleEditSaveMode();
    }

    // ==================== TOP FILMSTRIP PANEL & SORTING HANDLERS ====================

    private static int CompareItems(ImageItem a, ImageItem b, ImageSortMode mode)
    {
        int cmp = mode switch
        {
            ImageSortMode.NameDesc => NaturalStringComparer.Instance.Compare(b.FileName, a.FileName),
            ImageSortMode.DateDesc => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc),
            ImageSortMode.DateAsc => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc),
            ImageSortMode.SizeDesc => b.FileSizeBytes.CompareTo(a.FileSizeBytes),
            _ => NaturalStringComparer.Instance.Compare(a.FileName, b.FileName)
        };
        return cmp != 0 ? cmp : NaturalStringComparer.Instance.Compare(a.FileName, b.FileName);
    }

    private static void SortItemsList(List<ImageItem> list, ImageSortMode mode)
    {
        list.Sort((a, b) => CompareItems(a, b, mode));
    }

    private void RefreshItemIndicesAndFolderSummary()
    {
        long totalBytes = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].DisplayIndex = i + 1;
            totalBytes += _items[i].FileSizeBytes;
        }
        _totalFolderBytes = totalBytes;
    }

    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSortCombo || sender is not ComboBox cb || cb.SelectedIndex < 0)
            return;

        var newMode = (ImageSortMode)Math.Clamp(cb.SelectedIndex, 0, 4);

        _isSyncingSortCombo = true;
        try
        {
            if (FilmstripSortComboBox != null) FilmstripSortComboBox.SelectedIndex = (int)newMode;
            if (OverviewSortComboBox != null) OverviewSortComboBox.SelectedIndex = (int)newMode;
        }
        finally
        {
            _isSyncingSortCombo = false;
        }

        ApplySortMode(newMode);

        string label = newMode switch
        {
            ImageSortMode.NameDesc => "Řazení: Název Z–A",
            ImageSortMode.DateDesc => "Řazení: Datum (Nejnovější první)",
            ImageSortMode.DateAsc => "Řazení: Datum (Nejstarší první)",
            ImageSortMode.SizeDesc => "Řazení: Velikost (Největší)",
            _ => "Řazení: Název A–Z (Přirozené)"
        };
        ShowToast($"🔀 {label}");
    }

    private void ApplySortMode(ImageSortMode newMode)
    {
        _settings.SortMode = newMode;
        AppSettingsService.Save(_settings);

        if (_items.Count == 0)
            return;

        string? activePath = (_currentIndex >= 0 && _currentIndex < _items.Count)
            ? _items[_currentIndex].FilePath
            : null;

        var sorted = _items.ToList();
        SortItemsList(sorted, newMode);

        _isSortingItems = true;
        try
        {
            _items.Clear();
            int newIndex = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                _items.Add(sorted[i]);
                if (activePath != null && string.Equals(sorted[i].FilePath, activePath, StringComparison.OrdinalIgnoreCase))
                {
                    newIndex = i;
                }
            }

            RefreshItemIndicesAndFolderSummary();
            _currentIndex = newIndex;
            ThumbnailListBox.SelectedIndex = _currentIndex;
            FilmstripListBox.SelectedIndex = _currentIndex;
        }
        finally
        {
            _isSortingItems = false;
        }

        UpdateFilmstripNavigationButtons();
        UpdateStatusText();

        if (TopFilmstripPanel.Visibility == Visibility.Visible)
        {
            ScrollFilmstripToCurrentItem(alignStart: _currentIndex == 0);
        }

        if (_isOverviewMode && _currentIndex >= 0 && _currentIndex < _items.Count)
        {
            ThumbnailListBox.ScrollIntoView(_items[_currentIndex]);
        }

        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        _ = LoadThumbnailsOutwardAsync(Math.Max(0, _currentIndex), _thumbnailCts.Token);
    }

    private void UpdateFilmstripNavigationButtons()
    {
        bool hasItems = _items.Count > 0 && _currentIndex >= 0;
        if (FilmstripFirstButton != null) FilmstripFirstButton.IsEnabled = hasItems && _currentIndex > 0;
        if (FilmstripPrevButton != null) FilmstripPrevButton.IsEnabled = hasItems && _currentIndex > 0;
        if (FilmstripNextButton != null) FilmstripNextButton.IsEnabled = hasItems && _currentIndex < _items.Count - 1;
        if (FilmstripLastButton != null) FilmstripLastButton.IsEnabled = hasItems && _currentIndex < _items.Count - 1;
        if (FilmstripCenterButton != null) FilmstripCenterButton.IsEnabled = hasItems;
    }

    private void UpdateFilmstripPinButtonUI()
    {
        if (FilmstripPinButton == null)
            return;

        if (_settings.PinTopFilmstrip)
        {
            FilmstripPinButton.Content = "📌 Připnuto";
            FilmstripPinButton.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
            FilmstripPinButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
            FilmstripPinButton.Foreground = Brushes.White;
        }
        else
        {
            FilmstripPinButton.Content = "📌 Připnout";
            FilmstripPinButton.Background = new SolidColorBrush(Color.FromRgb(0x27, 0x29, 0x32));
            FilmstripPinButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x3C, 0x4A));
            FilmstripPinButton.Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
        }
    }

    private void ApplyFilmstripScale(int percent, bool saveSetting = true)
    {
        int target = percent <= 0 ? 100 : percent;
        int normalized = FilmstripScaleSteps
            .OrderBy(step => Math.Abs(step - target))
            .First();

        _settings.FilmstripScalePercent = normalized;
        if (saveSetting)
        {
            AppSettingsService.Save(_settings);
        }

        int stepIndex = Array.IndexOf(FilmstripScaleSteps, normalized);
        if (stepIndex >= 0 && FilmstripSizeComboBox != null)
        {
            _isSyncingFilmstripSizeCombo = true;
            try
            {
                FilmstripSizeComboBox.SelectedIndex = stepIndex;
            }
            finally
            {
                _isSyncingFilmstripSizeCombo = false;
            }
        }

        if (Resources["FilmstripCardScaleTransform"] is ScaleTransform scaleTransform)
        {
            double factor = normalized / 100.0;
            scaleTransform.ScaleX = factor;
            scaleTransform.ScaleY = factor;
        }

        if (TopFilmstripPanel.Visibility == Visibility.Visible)
        {
            ScrollFilmstripToCurrentItem();
        }
    }

    private void FilmstripSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingFilmstripSizeCombo || FilmstripSizeComboBox == null)
            return;

        int idx = FilmstripSizeComboBox.SelectedIndex;
        if (idx < 0 || idx >= FilmstripScaleSteps.Length)
            return;

        int step = FilmstripScaleSteps[idx];
        ApplyFilmstripScale(step, saveSetting: true);
        ShowToast($"🔍 Velikost náhledů: {step} %");
    }

    private void ScrollFilmstripToCurrentItem(bool alignStart = false)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_currentIndex < 0 || _currentIndex >= _items.Count)
                return;

            ScrollViewer? sv = FindVisualChild<ScrollViewer>(FilmstripListBox);
            if (sv != null && sv.ViewportWidth > 0)
            {
                double scale = Math.Clamp(_settings.FilmstripScalePercent, 75, 200) / 100.0;
                double itemWidth = (168.0 * scale) + 8.0;
                double targetOffset = alignStart
                    ? 0.0
                    : Math.Max(0.0, (_currentIndex * itemWidth) - (sv.ViewportWidth / 2.0) + (itemWidth / 2.0));
                sv.ScrollToHorizontalOffset(targetOffset);
            }
            else
            {
                FilmstripListBox.ScrollIntoView(_items[_currentIndex]);
            }
        }), DispatcherPriority.Loaded);
    }

    private void TopFilmstripPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_settings.PinTopFilmstrip || FilmstripSortComboBox.IsDropDownOpen || FilmstripSizeComboBox.IsDropDownOpen)
            return;

        Point pos = e.GetPosition(FullscreenViewGrid);
        if (pos.Y > TopFilmstripPanel.ActualHeight + 6 || !TopFilmstripPanel.IsMouseOver)
        {
            TopFilmstripPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void TopFilmstripPanel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void FilmstripListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            int currentIdx = Array.IndexOf(FilmstripScaleSteps, _settings.FilmstripScalePercent);
            if (currentIdx < 0)
            {
                currentIdx = Array.IndexOf(FilmstripScaleSteps, 100);
            }

            int nextIdx = Math.Clamp(currentIdx + (e.Delta > 0 ? 1 : -1), 0, FilmstripScaleSteps.Length - 1);
            if (nextIdx != currentIdx)
            {
                int newStep = FilmstripScaleSteps[nextIdx];
                ApplyFilmstripScale(newStep, saveSetting: true);
                ShowToast($"🔍 Velikost náhledů: {newStep} %");
            }
            return;
        }

        ScrollViewer? sv = FindVisualChild<ScrollViewer>(FilmstripListBox);
        if (sv != null)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - (e.Delta * 1.25));
        }
    }

    private void FilmstripListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSortingItems)
            return;

        if (FilmstripListBox.SelectedIndex >= 0 && FilmstripListBox.SelectedIndex < _items.Count)
        {
            int newIdx = FilmstripListBox.SelectedIndex;
            if (newIdx != _currentIndex)
            {
                SetCurrentIndex(newIdx);
            }
        }
    }

    private void FilmstripItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem lbi && lbi.DataContext is ImageItem item && item.Thumbnail == null)
        {
            _ = _cacheService.EnsureThumbnailLoadedAsync(item);
        }
    }

    private void FilmstripFirstButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
            return;

        SetCurrentIndex(0);
        ScrollFilmstripToCurrentItem(alignStart: true);
        ShowToast("⏮ Skok na 1. fotku ve složce");
    }

    private void FilmstripLastButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
            return;

        SetCurrentIndex(_items.Count - 1);
        ScrollFilmstripToCurrentItem();
        ShowToast($"⏭ Skok na poslední fotku (#{_items.Count})");
    }

    private void FilmstripCenterButton_Click(object sender, RoutedEventArgs e)
    {
        ScrollFilmstripToCurrentItem();
    }

    private void FilmstripPinButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PinTopFilmstrip = !_settings.PinTopFilmstrip;
        AppSettingsService.Save(_settings);
        UpdateFilmstripPinButtonUI();

        if (_settings.PinTopFilmstrip)
        {
            TopFilmstripPanel.Visibility = Visibility.Visible;
            ScrollFilmstripToCurrentItem();
            ShowToast("📌 Horní pás miniatur připnut");
        }
        else
        {
            ShowToast("📌 Horní pás miniatur uvolněn (auto-schování)");
        }
    }

    // ==================== RIGHT-EDGE EXIF & DETAILS PANEL HANDLERS ====================

    private void UpdateRightExifPanel()
    {
        if (RightExifPanel == null)
            return;

        if (_items.Count == 0 || _currentIndex < 0 || _currentIndex >= _items.Count)
        {
            _currentMetadata = null;
            ExifFileNameText.Text = "Žádný obrázek";
            ExifLocationText.Text = "—";
            ExifFileTypeText.Text = "—";
            ExifFileSizeText.Text = "—";
            ExifFileDateText.Text = "—";
            ExifDimensionsText.Text = "—";
            ExifPrintSizeText.Text = "—";
            ExifQuickSummaryBorder.Visibility = Visibility.Collapsed;
            ExifQuickSummaryText.Text = string.Empty;

            ExifMakeText.Text = "—";
            ExifModelText.Text = "—";
            ExifLensText.Text = "—";
            ExifSoftwareText.Text = "—";
            ExifDateTakenText.Text = "—";
            ExifExposureTimeText.Text = "—";
            ExifFNumberText.Text = "—";
            ExifIsoText.Text = "—";
            ExifExposureProgramText.Text = "—";
            ExifMeteringModeText.Text = "—";
            ExifExposureBiasText.Text = "—";
            ExifFlashText.Text = "—";
            ExifFocalLengthText.Text = "—";
            ExifFocal35mmText.Text = "—";
            ExifWhiteBalanceColorText.Text = "—";
            ExifOrientationText.Text = "—";

            ExifGpsCoordsText.Text = "Bez GPS souřadnic";
            ExifGpsCoordsText.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            ExifGpsActionsPanel.Visibility = Visibility.Collapsed;
            ExifGpsBorder.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x2D));
            ExifGpsBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x37, 0x48));
            return;
        }

        var current = _items[_currentIndex];
        var bmp = FullscreenImage.Source as BitmapSource;
        PhotoDetailedMetadata meta = ExifMetadataService.GetMetadata(
            current.FilePath,
            bmp?.PixelWidth ?? 0,
            bmp?.PixelHeight ?? 0);
        _currentMetadata = meta;

        ExifFileNameText.Text = meta.FileName;
        ExifFileNameText.ToolTip = meta.FilePath;
        ExifLocationText.Text = meta.FolderPath;
        ExifLocationText.ToolTip = meta.FolderPath;
        ExifFileTypeText.Text = meta.FileTypeDescription;
        ExifFileSizeText.Text = meta.FileSizeFormatted;
        ExifFileDateText.Text = meta.FileDateFormatted;
        ExifDimensionsText.Text = meta.DimensionsAndMpFormatted;
        ExifPrintSizeText.Text = meta.PrintSizeAndDpiFormatted;

        if (!string.IsNullOrWhiteSpace(meta.QuickSummaryText))
        {
            ExifQuickSummaryText.Text = meta.QuickSummaryText;
            ExifQuickSummaryBorder.Visibility = Visibility.Visible;
        }
        else
        {
            ExifQuickSummaryText.Text = string.Empty;
            ExifQuickSummaryBorder.Visibility = Visibility.Collapsed;
        }

        ExifMakeText.Text = meta.Make;
        ExifModelText.Text = meta.Model;
        ExifLensText.Text = meta.LensModel;
        ExifSoftwareText.Text = meta.Software;
        ExifDateTakenText.Text = meta.DateTaken;
        ExifExposureTimeText.Text = meta.ExposureTime;
        ExifFNumberText.Text = meta.FNumber;
        ExifIsoText.Text = meta.IsoSpeed;
        ExifExposureProgramText.Text = meta.ExposureProgram;
        ExifMeteringModeText.Text = meta.MeteringMode;
        ExifExposureBiasText.Text = meta.ExposureBias;
        ExifFlashText.Text = meta.Flash;
        ExifFocalLengthText.Text = meta.FocalLength;
        ExifFocal35mmText.Text = meta.FocalLength35mm;
        ExifWhiteBalanceColorText.Text = meta.WhiteBalanceAndColorSpace;
        ExifOrientationText.Text = meta.OrientationText;

        ExifGpsCoordsText.Text = meta.GpsDisplayText;
        if (meta.HasGps)
        {
            ExifGpsCoordsText.Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC));
            ExifGpsActionsPanel.Visibility = Visibility.Visible;
            ExifGpsBorder.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x26, 0x21));
            ExifGpsBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        }
        else
        {
            ExifGpsCoordsText.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            ExifGpsActionsPanel.Visibility = Visibility.Collapsed;
            ExifGpsBorder.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x2D));
            ExifGpsBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x37, 0x48));
        }
    }

    private void UpdateRightExifPinButtonUI()
    {
        if (RightExifPinButton == null)
            return;

        if (_settings.PinRightExifPanel)
        {
            RightExifPinButton.Content = "📌 Připnuto";
            RightExifPinButton.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
            RightExifPinButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
            RightExifPinButton.Foreground = Brushes.White;
        }
        else
        {
            RightExifPinButton.Content = "📌 Připnout";
            RightExifPinButton.Background = new SolidColorBrush(Color.FromRgb(0x27, 0x29, 0x32));
            RightExifPinButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x3C, 0x4A));
            RightExifPinButton.Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
        }
    }

    private void ToggleRightExifPanel()
    {
        if (RightExifPanel.Visibility == Visibility.Visible)
        {
            _settings.PinRightExifPanel = false;
            AppSettingsService.Save(_settings);
            UpdateRightExifPinButtonUI();
            RightExifPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _settings.PinRightExifPanel = true;
            AppSettingsService.Save(_settings);
            UpdateRightExifPinButtonUI();
            RightExifPanel.Visibility = Visibility.Visible;
            UpdateRightExifPanel();
        }
    }

    private void RightExifPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_settings.PinRightExifPanel)
            return;

        var (vw, _) = GetViewportSize();
        Point pos = e.GetPosition(FullscreenViewGrid);
        if (pos.X < vw - RightExifPanel.ActualWidth - 6 || !RightExifPanel.IsMouseOver)
        {
            RightExifPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RightExifPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            return;

        e.Handled = true;
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

    private void RightExifPinButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PinRightExifPanel = !_settings.PinRightExifPanel;
        AppSettingsService.Save(_settings);
        UpdateRightExifPinButtonUI();

        if (_settings.PinRightExifPanel)
        {
            RightExifPanel.Visibility = Visibility.Visible;
            UpdateRightExifPanel();
            ShowToast("📌 Pravý EXIF panel připnut");
        }
        else
        {
            ShowToast("📌 Pravý EXIF panel uvolněn (auto-schování)");
        }
    }

    private void FlyoutExifPanelButton_Click(object sender, RoutedEventArgs e)
    {
        LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        ToggleRightExifPanel();
    }

    private void ExifCopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMetadata != null && !string.IsNullOrWhiteSpace(_currentMetadata.FilePath))
        {
            TryCopyToClipboard(_currentMetadata.FilePath, "📋 Cesta k souboru zkopírována do schránky");
        }
    }

    private void ExifCopySummaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMetadata == null)
            return;

        var m = _currentMetadata;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Soubor: {m.FileName}");
        sb.AppendLine($"Cesta: {m.FilePath}");
        sb.AppendLine($"Typ: {m.FileTypeDescription}");
        sb.AppendLine($"Velikost: {m.FileSizeFormatted}");
        sb.AppendLine($"Datum souboru: {m.FileDateFormatted}");
        sb.AppendLine($"Rozměry: {m.DimensionsAndMpFormatted}");
        sb.AppendLine($"Tisk / DPI: {m.PrintSizeAndDpiFormatted}");
        sb.AppendLine($"Fotoaparát: {m.Make} {m.Model}".Trim());
        if (m.LensModel != "—") sb.AppendLine($"Objektiv: {m.LensModel}");
        if (m.DateTaken != "—") sb.AppendLine($"Datum pořízení: {m.DateTaken}");
        if (!string.IsNullOrWhiteSpace(m.QuickSummaryText)) sb.AppendLine($"Expozice: {m.QuickSummaryText}");
        if (m.HasGps) sb.AppendLine($"GPS: {m.GpsDisplayText} ({m.GoogleMapsUrl})");

        TryCopyToClipboard(sb.ToString().TrimEnd(), "📋 Souhrn EXIF zkopírován do schránky");
    }

    private void ExifOpenExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMetadata == null || string.IsNullOrWhiteSpace(_currentMetadata.FilePath) || !File.Exists(_currentMetadata.FilePath))
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_currentMetadata.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Nelze otevřít Průzkumník: {ex.Message}");
        }
    }

    private void ExifOpenMapyCzButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMetadata is { HasGps: true } && !string.IsNullOrWhiteSpace(_currentMetadata.MapyCzUrl))
        {
            OpenExternalUrl(_currentMetadata.MapyCzUrl);
        }
    }

    private void ExifOpenGoogleMapsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMetadata is { HasGps: true } && !string.IsNullOrWhiteSpace(_currentMetadata.GoogleMapsUrl))
        {
            OpenExternalUrl(_currentMetadata.GoogleMapsUrl);
        }
    }

    private void ExifCopyGpsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMetadata is { HasGps: true } && !string.IsNullOrWhiteSpace(_currentMetadata.GpsClipboardText))
        {
            TryCopyToClipboard(_currentMetadata.GpsClipboardText, $"📍 Souřadnice {_currentMetadata.GpsClipboardText} zkopírovány");
        }
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Nelze otevřít prohlížeč: {ex.Message}");
        }
    }

    private void TryCopyToClipboard(string text, string successToast)
    {
        try
        {
            Clipboard.SetText(text);
            ShowToast(successToast);
        }
        catch (Exception ex)
        {
            ShowToast($"Chyba schránky: {ex.Message}");
        }
    }

    // ==================== LEFT-EDGE FLYOUT PANEL HANDLERS ====================

    private void LeftFlyoutPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(FullscreenViewGrid);
        if (pos.X > LeftFlyoutPanel.ActualWidth + 6 || !LeftFlyoutPanel.IsMouseOver)
        {
            LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void FlyoutPrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex > 0)
            SetCurrentIndex(_currentIndex - 1);
    }

    private void FlyoutNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < _items.Count - 1)
            SetCurrentIndex(_currentIndex + 1);
    }

    private void FlyoutOverviewButton_Click(object sender, RoutedEventArgs e)
    {
        LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        if (_items.Count > 0)
            SwitchMode(overviewMode: true);
    }

    private void FlyoutZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        var (vw, vh) = GetViewportSize();
        ZoomAtPoint(_currentScale * ZoomStepFactor, new Point(vw / 2.0, vh / 2.0));
    }

    private void FlyoutZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        var (vw, vh) = GetViewportSize();
        _fitScale = CalculateFitScale();
        double nextScale = _currentScale / ZoomStepFactor;
        if (!_isFitToWindow && _currentScale > _fitScale && nextScale <= _fitScale * 1.02)
        {
            ApplyFitToWindow();
        }
        else
        {
            ZoomAtPoint(nextScale, new Point(vw / 2.0, vh / 2.0));
        }
    }

    private void FlyoutZoom100Button_Click(object sender, RoutedEventArgs e)
    {
        var (vw, vh) = GetViewportSize();
        ToggleOneToOneZoom(new Point(vw / 2.0, vh / 2.0));
    }

    private void FlyoutZoomFitButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyFitToWindow();
    }

    private void FlyoutRotateLeftButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ApplyPhysicalTransformAsync(ImageTransformType.RotateLeft);
    }

    private void FlyoutRotateRightButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ApplyPhysicalTransformAsync(ImageTransformType.RotateRight);
    }

    private void FlyoutFlipHButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ApplyPhysicalTransformAsync(ImageTransformType.FlipHorizontal);
    }

    private void FlyoutFlipVButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ApplyPhysicalTransformAsync(ImageTransformType.FlipVertical);
    }

    private void FlyoutCropButton_Click(object sender, RoutedEventArgs e)
    {
        LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        EnterCropMode();
    }

    private void FlyoutCopyButton_Click(object sender, RoutedEventArgs e)
    {
        LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        ShowCopyFileDialog();
    }

    private void FlyoutDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteCurrentFileToRecycleBin();
    }

    // ==================== PHASE 3: ROTATE, FLIP & CROP ====================

    private async Task ApplyPhysicalTransformAsync(ImageTransformType transformType)
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count)
            return;

        ExitCropMode();
        EndMagnifierOrPan();

        ImageItem currentItem = _items[_currentIndex];
        string filePath = currentItem.FilePath;

        BitmapSource? currentBitmap = FullscreenImage.Source as BitmapSource
            ?? await _cacheService.GetOrLoadImageAsync(filePath);

        if (currentBitmap == null)
            return;

        if (!_hasUnsavedTransform)
        {
            _untransformedOriginalBitmap = currentBitmap;
            _hasUnsavedTransform = true;
        }

        BitmapSource transformed = ImageTransformService.ApplyTransform(currentBitmap, transformType);
        SetFullscreenBitmap(transformed);

        string actionLabel = transformType switch
        {
            ImageTransformType.RotateLeft => "Otočeno o 90° doleva (uloží se při odchodu z fotky)",
            ImageTransformType.RotateRight => "Otočeno o 90° doprava (uloží se při odchodu z fotky)",
            ImageTransformType.FlipHorizontal => "Horizontálně převráceno (uloží se při odchodu z fotky)",
            ImageTransformType.FlipVertical => "Vertikálně převráceno (uloží se při odchodu z fotky)",
            _ => "Upraveno"
        };
        ShowToast(actionLabel);
        UpdateStatusText();
    }

    private bool ResolvePendingTransformBeforeLeave()
    {
        if (!_hasUnsavedTransform || _isResolvingTransform)
            return true;

        if (_currentIndex < 0 || _currentIndex >= _items.Count || FullscreenImage.Source is not BitmapSource transformedBitmap)
        {
            _hasUnsavedTransform = false;
            _untransformedOriginalBitmap = null;
            return true;
        }

        _isResolvingTransform = true;
        try
        {
            ImageItem currentItem = _items[_currentIndex];
            string originalFilePath = currentItem.FilePath;
            string rotatedFilePath = GetUniqueRotatedFilePath(originalFilePath);

            var dlg = new SaveTransformWindow(originalFilePath, Path.GetFileName(rotatedFilePath), _settings.UseSaveAsDialogForEdits)
            {
                Owner = this
            };

            if (dlg.ShowDialog() != true || dlg.Choice == SaveTransformChoice.Cancel)
            {
                return false;
            }

            switch (dlg.Choice)
            {
                case SaveTransformChoice.SaveAsNewRotated:
                    string targetSavePath = rotatedFilePath;
                    if (_settings.UseSaveAsDialogForEdits)
                    {
                        string ext = Path.GetExtension(rotatedFilePath).ToLowerInvariant();
                        var saveDlg = new SaveFileDialog
                        {
                            Title = "Uložit otočenou / převrácenou fotografii jako...",
                            InitialDirectory = Path.GetDirectoryName(rotatedFilePath) ?? _currentFolder,
                            FileName = Path.GetFileName(rotatedFilePath),
                            Filter = ImageCacheService.SaveFileDialogFilter,
                            FilterIndex = ImageCacheService.GetSaveFilterIndex(ext)
                        };

                        if (saveDlg.ShowDialog(this) != true)
                        {
                            return false;
                        }
                        targetSavePath = saveDlg.FileName;
                    }

                    ImageTransformService.SaveBitmapToFile(transformedBitmap, targetSavePath, quality: 95);
                    if (_untransformedOriginalBitmap != null)
                    {
                        _cacheService.UpdateCached(originalFilePath, _untransformedOriginalBitmap);
                        currentItem.Thumbnail = ImageTransformService.CreateThumbnail(_untransformedOriginalBitmap);
                        SetFullscreenBitmap(_untransformedOriginalBitmap);
                    }
                    _hasUnsavedTransform = false;
                    _untransformedOriginalBitmap = null;
                    InsertNewFileIfInCurrentFolder(targetSavePath);
                    ShowToast($"Uloženo jako nová: {Path.GetFileName(targetSavePath)}");
                    return true;

                case SaveTransformChoice.OverwriteOriginal:
                    ImageTransformService.SaveBitmapToFile(transformedBitmap, originalFilePath, quality: 95);
                    _cacheService.UpdateCached(originalFilePath, transformedBitmap);
                    currentItem.Thumbnail = ImageTransformService.CreateThumbnail(transformedBitmap);
                    _hasUnsavedTransform = false;
                    _untransformedOriginalBitmap = null;
                    ShowToast($"Originál přepsán: {Path.GetFileName(originalFilePath)}");
                    return true;

                case SaveTransformChoice.DiscardChanges:
                    if (_untransformedOriginalBitmap != null)
                    {
                        SetFullscreenBitmap(_untransformedOriginalBitmap);
                    }
                    _hasUnsavedTransform = false;
                    _untransformedOriginalBitmap = null;
                    ShowToast("Úpravy zahozeny");
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Chyba při ukládání: {ex.Message}");
            return false;
        }
        finally
        {
            _isResolvingTransform = false;
        }
    }

    private static string GetUniqueRotatedFilePath(string originalFilePath)
    {
        string dir = Path.GetDirectoryName(originalFilePath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(originalFilePath);
        string ext = ImageCacheService.NormalizeWritableExtension(Path.GetExtension(originalFilePath));

        string candidate = Path.Combine(dir, $"{baseName}_rotated{ext}");
        int counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{baseName}_rotated_{counter}{ext}");
            counter++;
        }
        return candidate;
    }

    private void EnterCropMode()
    {
        if (_isOverviewMode || FullscreenImage.Source == null || _currentIndex < 0)
            return;

        EndMagnifierOrPan();
        LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        RightExifPanel.Visibility = Visibility.Collapsed;
        TopFilmstripPanel.Visibility = Visibility.Collapsed;
        _isCropMode = true;
        _hasCropSelection = false;
        _cropDragAction = CropDragAction.None;
        _cropRect = Rect.Empty;

        CropSelectionContainer.Visibility = Visibility.Collapsed;
        CropSizeBadge.Visibility = Visibility.Collapsed;
        CropActionBar.Visibility = Visibility.Collapsed;
        CropHintBannerText.Text = "Režim výstřižku (X): Tažením myší vyberte oblast   •   Esc: Ukončit výstřižek";
        CropHintBanner.Visibility = Visibility.Visible;
        CropOverlayCanvas.Cursor = Cursors.Cross;
        CropOverlayCanvas.Visibility = Visibility.Visible;

        SetFullDarkMask();
    }

    private void ExitCropMode()
    {
        if (!_isCropMode)
            return;

        _isCropMode = false;
        _hasCropSelection = false;
        _cropDragAction = CropDragAction.None;
        CropOverlayCanvas.ReleaseMouseCapture();
        CropOverlayCanvas.Visibility = Visibility.Collapsed;
        CropHintBanner.Visibility = Visibility.Collapsed;
        CropSelectionContainer.Visibility = Visibility.Collapsed;
        CropSizeBadge.Visibility = Visibility.Collapsed;
        CropActionBar.Visibility = Visibility.Collapsed;

        if (_settings.PinTopFilmstrip && !_isOverviewMode && _items.Count > 0)
        {
            TopFilmstripPanel.Visibility = Visibility.Visible;
        }
        if (_settings.PinRightExifPanel && !_isOverviewMode && _items.Count > 0)
        {
            RightExifPanel.Visibility = Visibility.Visible;
            UpdateRightExifPanel();
        }
    }

    private void ResetCropSelection()
    {
        _hasCropSelection = false;
        _cropDragAction = CropDragAction.None;
        _cropRect = Rect.Empty;
        CropSelectionContainer.Visibility = Visibility.Collapsed;
        CropSizeBadge.Visibility = Visibility.Collapsed;
        CropActionBar.Visibility = Visibility.Collapsed;
        CropOverlayCanvas.Cursor = Cursors.Cross;
        CropHintBannerText.Text = "Režim výstřižku (X): Tažením myší vyberte oblast   •   Esc: Ukončit výstřižek";
        SetFullDarkMask();
    }

    private void SetFullDarkMask()
    {
        var (vw, vh) = GetViewportSize();
        Canvas.SetLeft(CropMaskTop, 0);
        Canvas.SetTop(CropMaskTop, 0);
        CropMaskTop.Width = vw;
        CropMaskTop.Height = vh;

        CropMaskBottom.Width = 0;
        CropMaskBottom.Height = 0;
        CropMaskLeft.Width = 0;
        CropMaskLeft.Height = 0;
        CropMaskRight.Width = 0;
        CropMaskRight.Height = 0;
    }

    private Rect GetDisplayedImageScreenBounds()
    {
        double imgLeft = _panX;
        double imgTop = _panY;
        double imgRight = _panX + (_imageBaseWidthDip * _currentScale);
        double imgBottom = _panY + (_imageBaseHeightDip * _currentScale);

        var (vw, vh) = GetViewportSize();
        double minX = Math.Max(0, imgLeft);
        double minY = Math.Max(0, imgTop);
        double maxX = Math.Min(vw, imgRight);
        double maxY = Math.Min(vh, imgBottom);

        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;

        return new Rect(new Point(minX, minY), new Point(maxX, maxY));
    }

    private Point ClampPointToDisplayedImage(Point pt)
    {
        Rect bounds = GetDisplayedImageScreenBounds();
        return new Point(
            Math.Clamp(pt.X, bounds.Left, bounds.Right),
            Math.Clamp(pt.Y, bounds.Top, bounds.Bottom));
    }

    private Int32Rect GetSelectedImagePixelRect(Rect screenRect)
    {
        if (FullscreenImage.Source is not BitmapSource bmp ||
            _imageBaseWidthDip <= 0 || _imageBaseHeightDip <= 0 || _currentScale <= 0)
        {
            return Int32Rect.Empty;
        }

        double scaledW = _imageBaseWidthDip * _currentScale;
        double scaledH = _imageBaseHeightDip * _currentScale;

        double u1 = Math.Clamp((screenRect.Left - _panX) / scaledW, 0.0, 1.0);
        double v1 = Math.Clamp((screenRect.Top - _panY) / scaledH, 0.0, 1.0);
        double u2 = Math.Clamp((screenRect.Right - _panX) / scaledW, 0.0, 1.0);
        double v2 = Math.Clamp((screenRect.Bottom - _panY) / scaledH, 0.0, 1.0);

        int x1 = (int)Math.Round(u1 * bmp.PixelWidth);
        int y1 = (int)Math.Round(v1 * bmp.PixelHeight);
        int x2 = (int)Math.Round(u2 * bmp.PixelWidth);
        int y2 = (int)Math.Round(v2 * bmp.PixelHeight);

        int w = Math.Max(0, x2 - x1);
        int h = Math.Max(0, y2 - y1);

        return new Int32Rect(x1, y1, w, h);
    }

    private CropDragAction HitTestCropRect(Point pos)
    {
        if (!_hasCropSelection || _cropRect.IsEmpty || _cropRect.Width < 4 || _cropRect.Height < 4)
            return CropDragAction.CreateNew;

        const double cornerTol = 10.0;
        const double edgeTol = 8.0;

        bool nearLeft = Math.Abs(pos.X - _cropRect.Left) <= cornerTol;
        bool nearRight = Math.Abs(pos.X - _cropRect.Right) <= cornerTol;
        bool nearTop = Math.Abs(pos.Y - _cropRect.Top) <= cornerTol;
        bool nearBottom = Math.Abs(pos.Y - _cropRect.Bottom) <= cornerTol;

        // 4 Corners first
        if (nearLeft && nearTop) return CropDragAction.ResizeNW;
        if (nearRight && nearTop) return CropDragAction.ResizeNE;
        if (nearLeft && nearBottom) return CropDragAction.ResizeSW;
        if (nearRight && nearBottom) return CropDragAction.ResizeSE;

        // 4 Edges
        bool withinX = pos.X >= _cropRect.Left - edgeTol && pos.X <= _cropRect.Right + edgeTol;
        bool withinY = pos.Y >= _cropRect.Top - edgeTol && pos.Y <= _cropRect.Bottom + edgeTol;

        if (withinX && Math.Abs(pos.Y - _cropRect.Top) <= edgeTol) return CropDragAction.ResizeN;
        if (withinX && Math.Abs(pos.Y - _cropRect.Bottom) <= edgeTol) return CropDragAction.ResizeS;
        if (withinY && Math.Abs(pos.X - _cropRect.Left) <= edgeTol) return CropDragAction.ResizeW;
        if (withinY && Math.Abs(pos.X - _cropRect.Right) <= edgeTol) return CropDragAction.ResizeE;

        // Inside selection -> Move entire rectangle
        if (_cropRect.Contains(pos))
            return CropDragAction.Move;

        return CropDragAction.CreateNew;
    }

    private static Cursor GetCursorForCropAction(CropDragAction action) => action switch
    {
        CropDragAction.ResizeNW or CropDragAction.ResizeSE => Cursors.SizeNWSE,
        CropDragAction.ResizeNE or CropDragAction.ResizeSW => Cursors.SizeNESW,
        CropDragAction.ResizeN or CropDragAction.ResizeS => Cursors.SizeNS,
        CropDragAction.ResizeE or CropDragAction.ResizeW => Cursors.SizeWE,
        CropDragAction.Move => Cursors.SizeAll,
        _ => Cursors.Cross
    };

    private void UpdateCropOverlayVisuals()
    {
        if (!_hasCropSelection || _cropRect.IsEmpty)
        {
            SetFullDarkMask();
            CropSelectionContainer.Visibility = Visibility.Collapsed;
            CropSizeBadge.Visibility = Visibility.Collapsed;
            CropActionBar.Visibility = Visibility.Collapsed;
            return;
        }

        var (vw, vh) = GetViewportSize();
        Rect rect = _cropRect;

        // Top mask
        Canvas.SetLeft(CropMaskTop, 0);
        Canvas.SetTop(CropMaskTop, 0);
        CropMaskTop.Width = vw;
        CropMaskTop.Height = Math.Max(0, rect.Top);

        // Bottom mask
        Canvas.SetLeft(CropMaskBottom, 0);
        Canvas.SetTop(CropMaskBottom, rect.Bottom);
        CropMaskBottom.Width = vw;
        CropMaskBottom.Height = Math.Max(0, vh - rect.Bottom);

        // Left mask
        Canvas.SetLeft(CropMaskLeft, 0);
        Canvas.SetTop(CropMaskLeft, rect.Top);
        CropMaskLeft.Width = Math.Max(0, rect.Left);
        CropMaskLeft.Height = rect.Height;

        // Right mask
        Canvas.SetLeft(CropMaskRight, rect.Right);
        Canvas.SetTop(CropMaskRight, rect.Top);
        CropMaskRight.Width = Math.Max(0, vw - rect.Right);
        CropMaskRight.Height = rect.Height;

        // Selection container (border + rule-of-thirds + 8 handles)
        Canvas.SetLeft(CropSelectionContainer, rect.Left);
        Canvas.SetTop(CropSelectionContainer, rect.Top);
        CropSelectionContainer.Width = rect.Width;
        CropSelectionContainer.Height = rect.Height;
        CropSelectionContainer.Visibility = Visibility.Visible;

        // TOP-LEFT: Pixel dimension badge
        Int32Rect pxRect = GetSelectedImagePixelRect(rect);
        CropSizeBadgeText.Text = $"{pxRect.Width} × {pxRect.Height} px";
        CropSizeBadge.Visibility = Visibility.Visible;
        CropSizeBadge.UpdateLayout();

        double barY = rect.Top >= 36
            ? rect.Top - 32
            : (rect.Bottom + 36 <= vh ? rect.Bottom + 8 : rect.Top + 8);
        barY = Math.Clamp(barY, 4, Math.Max(4, vh - 34));

        Canvas.SetLeft(CropSizeBadge, Math.Clamp(rect.Left, 4, Math.Max(4, vw - CropSizeBadge.ActualWidth - 4)));
        Canvas.SetTop(CropSizeBadge, barY);

        // TOP-RIGHT: Confirm (OK) & Reset buttons (shown when not actively dragging, or kept synced)
        if (_cropDragAction == CropDragAction.None)
        {
            CropActionBar.Visibility = Visibility.Visible;
            CropActionBar.UpdateLayout();

            double actionBarWidth = CropActionBar.ActualWidth > 0 ? CropActionBar.ActualWidth : 135;
            double minActionLeft = rect.Left + CropSizeBadge.ActualWidth + 8;
            double desiredActionLeft = Math.Max(minActionLeft, rect.Right - actionBarWidth);
            desiredActionLeft = Math.Clamp(desiredActionLeft, 4, Math.Max(4, vw - actionBarWidth - 4));

            Canvas.SetLeft(CropActionBar, desiredActionLeft);
            Canvas.SetTop(CropActionBar, barY);
        }
        else
        {
            CropActionBar.Visibility = Visibility.Collapsed;
        }
    }

    private void CropOverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCropMode || FullscreenImage.Source == null)
            return;

        // Ignore clicks that originated on the OK / Reset buttons inside CropActionBar
        if (e.OriginalSource is DependencyObject dep && IsDescendantOf(dep, CropActionBar))
            return;

        e.Handled = true;
        Point rawPos = e.GetPosition(CropOverlayCanvas);

        if (_hasCropSelection && e.ClickCount == 2 && _cropRect.Contains(rawPos))
        {
            ConfirmCurrentCropSelection();
            return;
        }

        CropDragAction hitAction = HitTestCropRect(rawPos);
        _cropDragAction = hitAction;
        _cropDragStartMouse = rawPos;

        if (hitAction == CropDragAction.CreateNew)
        {
            Point clamped = ClampPointToDisplayedImage(rawPos);
            _cropDragStartMouse = clamped;
            _cropRect = new Rect(clamped, clamped);
            _cropDragStartRect = _cropRect;
            _hasCropSelection = true;
        }
        else
        {
            _cropDragStartRect = _cropRect;
        }

        CropOverlayCanvas.Cursor = GetCursorForCropAction(hitAction);
        CropOverlayCanvas.CaptureMouse();
        UpdateCropOverlayVisuals();
    }

    private void CropOverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isCropMode)
            return;

        Point rawPos = e.GetPosition(CropOverlayCanvas);

        if (_cropDragAction == CropDragAction.None)
        {
            CropDragAction hoverAction = HitTestCropRect(rawPos);
            CropOverlayCanvas.Cursor = GetCursorForCropAction(hoverAction);
            return;
        }

        Rect imgBounds = GetDisplayedImageScreenBounds();
        const double minBoxSize = 12.0;

        if (_cropDragAction == CropDragAction.CreateNew)
        {
            Point currentClamped = ClampPointToDisplayedImage(rawPos);
            _cropRect = new Rect(_cropDragStartMouse, currentClamped);
        }
        else if (_cropDragAction == CropDragAction.Move)
        {
            double dx = rawPos.X - _cropDragStartMouse.X;
            double dy = rawPos.Y - _cropDragStartMouse.Y;

            double newLeft = Math.Clamp(_cropDragStartRect.Left + dx, imgBounds.Left, Math.Max(imgBounds.Left, imgBounds.Right - _cropDragStartRect.Width));
            double newTop = Math.Clamp(_cropDragStartRect.Top + dy, imgBounds.Top, Math.Max(imgBounds.Top, imgBounds.Bottom - _cropDragStartRect.Height));

            _cropRect = new Rect(newLeft, newTop, _cropDragStartRect.Width, _cropDragStartRect.Height);
        }
        else
        {
            // Resize via 4 corners or 4 edges
            double dx = rawPos.X - _cropDragStartMouse.X;
            double dy = rawPos.Y - _cropDragStartMouse.Y;

            double left = _cropDragStartRect.Left;
            double right = _cropDragStartRect.Right;
            double top = _cropDragStartRect.Top;
            double bottom = _cropDragStartRect.Bottom;

            if (_cropDragAction is CropDragAction.ResizeW or CropDragAction.ResizeNW or CropDragAction.ResizeSW)
            {
                left = Math.Clamp(left + dx, imgBounds.Left, right - minBoxSize);
            }
            if (_cropDragAction is CropDragAction.ResizeE or CropDragAction.ResizeNE or CropDragAction.ResizeSE)
            {
                right = Math.Clamp(right + dx, left + minBoxSize, imgBounds.Right);
            }
            if (_cropDragAction is CropDragAction.ResizeN or CropDragAction.ResizeNW or CropDragAction.ResizeNE)
            {
                top = Math.Clamp(top + dy, imgBounds.Top, bottom - minBoxSize);
            }
            if (_cropDragAction is CropDragAction.ResizeS or CropDragAction.ResizeSW or CropDragAction.ResizeSE)
            {
                bottom = Math.Clamp(bottom + dy, top + minBoxSize, imgBounds.Bottom);
            }

            _cropRect = new Rect(new Point(left, top), new Point(right, bottom));
        }

        UpdateCropOverlayVisuals();
    }

    private void CropOverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isCropMode || _cropDragAction == CropDragAction.None)
            return;

        e.Handled = true;
        CropOverlayCanvas.ReleaseMouseCapture();
        _cropDragAction = CropDragAction.None;

        Int32Rect pixelRect = GetSelectedImagePixelRect(_cropRect);
        if (pixelRect.Width < 6 || pixelRect.Height < 6)
        {
            ResetCropSelection();
            return;
        }

        CropHintBannerText.Text = "Upravte velikost za hrany/rohy nebo posuňte výřez   •   OK / Enter: Potvrdit   •   Esc: Konec";
        UpdateCropOverlayVisuals();
    }

    private void CropConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmCurrentCropSelection();
    }

    private void CropResetButton_Click(object sender, RoutedEventArgs e)
    {
        ResetCropSelection();
    }

    private void ConfirmCurrentCropSelection()
    {
        if (!_isCropMode || !_hasCropSelection || FullscreenImage.Source is not BitmapSource sourceBitmap)
            return;

        Int32Rect pixelRect = GetSelectedImagePixelRect(_cropRect);
        if (pixelRect.Width < 4 || pixelRect.Height < 4)
            return;

        BitmapSource cropped = ImageTransformService.CropBitmap(sourceBitmap, pixelRect);
        string currentFilePath = _items[_currentIndex].FilePath;

        var dlg = new CropActionWindow(cropped, currentFilePath, _settings.UseSaveAsDialogForEdits)
        {
            Owner = this
        };

        if (dlg.ShowDialog() == true)
        {
            ExitCropMode();

            if (dlg.ResultAction == CropResultAction.CopiedToClipboard)
            {
                ShowToast($"Výstřižek ({cropped.PixelWidth} × {cropped.PixelHeight} px) zkopírován do schránky");
            }
            else if (dlg.ResultAction == CropResultAction.SavedToFile && !string.IsNullOrWhiteSpace(dlg.SavedFilePath))
            {
                InsertNewFileIfInCurrentFolder(dlg.SavedFilePath);
                ShowToast($"Výstřižek uložen: {Path.GetFileName(dlg.SavedFilePath)}");
            }
        }
    }

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject parent)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, parent))
                return true;
            child = VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    private void InsertNewFileIfInCurrentFolder(string savedFilePath)
    {
        string fullSaved = Path.GetFullPath(savedFilePath);
        string? dir = Path.GetDirectoryName(fullSaved);
        if (!string.Equals(dir, _currentFolder, StringComparison.OrdinalIgnoreCase) ||
            !ImageCacheService.IsSupportedFile(fullSaved))
        {
            return;
        }

        // Check if already in _items
        if (_items.Any(x => string.Equals(x.FilePath, fullSaved, StringComparison.OrdinalIgnoreCase)))
            return;

        var newItem = new ImageItem(fullSaved);
        int insertIndex = _items.Count;
        for (int i = 0; i < _items.Count; i++)
        {
            if (CompareItems(newItem, _items[i], _settings.SortMode) < 0)
            {
                insertIndex = i;
                break;
            }
        }

        _items.Insert(insertIndex, newItem);
        RefreshItemIndicesAndFolderSummary();
        if (insertIndex <= _currentIndex)
        {
            _currentIndex++;
        }

        _ = _cacheService.EnsureThumbnailLoadedAsync(newItem);
        UpdateFilmstripNavigationButtons();
        UpdateStatusText();
    }

    // ==================== FILE OPERATIONS (COPY & DELETE TO RECYCLE BIN) ====================

    private void ShowCopyFileDialog()
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count)
            return;

        ExitCropMode();
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
            if (FolderTreeView.SelectedItem is FolderNode selectedNode)
            {
                selectedNode.EnsureChildrenLoaded(forceRefresh: true);
            }
            ShowToast($"Zkopírováno do: {dlg.SelectedTargetFolder}");
        }
    }

    private void BatchConvertButton_Click(object sender, RoutedEventArgs e)
    {
        LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        ShowBatchConvertDialog();
    }

    private void ShowBatchConvertDialog()
    {
        if (!ResolvePendingTransformBeforeLeave())
            return;

        ExitCropMode();
        EndMagnifierOrPan();

        List<string> initialFiles;
        if (_isOverviewMode && ThumbnailListBox.SelectedItems.Count > 1)
        {
            initialFiles = ThumbnailListBox.SelectedItems
                .OfType<ImageItem>()
                .Select(x => x.FilePath)
                .ToList();
        }
        else
        {
            initialFiles = _items.Select(x => x.FilePath).ToList();
        }

        var dlg = new BatchConvertWindow(initialFiles, _currentFolder, _settings.LastBatchOutputFolder)
        {
            Owner = this
        };

        dlg.ShowDialog();

        if (!string.IsNullOrWhiteSpace(dlg.LastUsedOutputFolder))
        {
            _settings.LastBatchOutputFolder = dlg.LastUsedOutputFolder;
            AppSettingsService.Save(_settings);
            if (FolderTreeView.SelectedItem is FolderNode selectedNode)
            {
                selectedNode.EnsureChildrenLoaded(forceRefresh: true);
            }
        }

        if (dlg.AnyConvertedInCurrentFolder && !string.IsNullOrWhiteSpace(_currentFolder))
        {
            string? selectedFile = (_currentIndex >= 0 && _currentIndex < _items.Count)
                ? _items[_currentIndex].FilePath
                : null;
            LoadFolder(_currentFolder, selectedFile, startInFullscreen: !_isOverviewMode);
        }
    }

    private void DeleteCurrentFileToRecycleBin()
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count)
            return;

        ExitCropMode();
        EndMagnifierOrPan();
        _hasUnsavedTransform = false;
        _untransformedOriginalBitmap = null;
        int indexToDelete = _currentIndex;
        string filePath = _items[indexToDelete].FilePath;

        if (!FileOperationsService.MoveToRecycleBin(filePath))
            return;

        _cacheService.Invalidate(filePath);
        _items.RemoveAt(indexToDelete);
        RefreshItemIndicesAndFolderSummary();

        if (_items.Count == 0)
        {
            _currentIndex = -1;
            FullscreenImage.Source = null;
            OverviewPreviewImage.Source = null;
            OverviewPreviewMetaText.Text = string.Empty;
            TopFilmstripPanel.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
            ThumbnailListBox.Visibility = Visibility.Collapsed;
            UpdateFilmstripNavigationButtons();
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

    private void SwitchMode(bool overviewMode, bool keepTreeFocus = false)
    {
        if (overviewMode && !_isOverviewMode && !ResolvePendingTransformBeforeLeave())
            return;

        bool wasInFullscreen = !_isOverviewMode;
        ExitCropMode();
        EndMagnifierOrPan();
        LeftFlyoutPanel.Visibility = Visibility.Collapsed;
        _isOverviewMode = overviewMode;

        if (_isOverviewMode)
        {
            TopFilmstripPanel.Visibility = Visibility.Collapsed;
            RightExifPanel.Visibility = Visibility.Collapsed;
            FullscreenViewGrid.Visibility = Visibility.Collapsed;
            OverviewGrid.Visibility = Visibility.Visible;
            OverviewGrid.UpdateLayout();

            if (wasInFullscreen && !string.IsNullOrWhiteSpace(_currentFolder))
            {
                SyncFolderTreeToPath(_currentFolder);
            }

            if (_currentIndex >= 0 && _currentIndex < _items.Count)
            {
                ThumbnailListBox.SelectedIndex = _currentIndex;
                var selectedItem = _items[_currentIndex];
                ThumbnailListBox.ScrollIntoView(selectedItem);
                ThumbnailListBox.UpdateLayout();

                if (!keepTreeFocus && !FolderTreeView.IsKeyboardFocusWithin)
                {
                    if (ThumbnailListBox.ItemContainerGenerator.ContainerFromIndex(_currentIndex) is ListBoxItem container)
                    {
                        container.Focus();
                    }
                    else
                    {
                        ThumbnailListBox.Focus();
                    }
                }
            }
            else if (!keepTreeFocus && !FolderTreeView.IsKeyboardFocusWithin)
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

            if (_settings.PinTopFilmstrip)
            {
                TopFilmstripPanel.Visibility = Visibility.Visible;
                ScrollFilmstripToCurrentItem(alignStart: _currentIndex == 0);
            }
            else
            {
                TopFilmstripPanel.Visibility = Visibility.Collapsed;
            }

            if (_settings.PinRightExifPanel)
            {
                RightExifPanel.Visibility = Visibility.Visible;
                UpdateRightExifPanel();
            }
            else
            {
                RightExifPanel.Visibility = Visibility.Collapsed;
            }

            Focus();
        }
    }

    private void UpdateStatusText()
    {
        if (_items.Count == 0 || _currentIndex < 0 || _currentIndex >= _items.Count)
        {
            StatusCounterText.Text = "0 obrázků";
            FlyoutFileNameText.Text = "Žádný obrázek";
            FlyoutFileDetailsText.Text = "0 / 0";
            OverviewPreviewMetaText.Text = string.Empty;
            FilmstripCounterText.Text = "0 / 0 fotek";
            FilmstripResolutionText.Text = string.Empty;
            if (RightExifPanel.Visibility == Visibility.Visible)
            {
                UpdateRightExifPanel();
            }
            return;
        }

        var current = _items[_currentIndex];
        string sizeInfo = string.Empty;
        string compactSize = string.Empty;
        try
        {
            var fi = new FileInfo(current.FilePath);
            if (fi.Exists)
            {
                double kb = fi.Length / 1024.0;
                compactSize = kb >= 1024.0 ? $"{kb / 1024.0:F2} MB" : $"{kb:F0} KB";
                sizeInfo = $"   ({compactSize})";
            }
        }
        catch
        {
            // Ignore file info errors
        }

        string resolutionInfo = string.Empty;
        string compactRes = string.Empty;
        if (FullscreenImage.Source is BitmapSource bmp)
        {
            compactRes = $"{bmp.PixelWidth} × {bmp.PixelHeight} px";
            resolutionInfo = $"   •   {compactRes}";
        }

        double totalMb = _totalFolderBytes / (1024.0 * 1024.0);
        string totalSizeText = totalMb >= 1024.0
            ? $"{totalMb / 1024.0:F2} GB"
            : $"{totalMb:F1} MB";

        StatusCounterText.Text = $"{_currentIndex + 1} / {_items.Count}   —   {current.FileName}{resolutionInfo}{sizeInfo}";
        FlyoutFileNameText.Text = current.FileName;
        FlyoutFileDetailsText.Text = $"{_currentIndex + 1} / {_items.Count}"
            + (string.IsNullOrEmpty(compactRes) ? "" : $"  •  {compactRes}")
            + (string.IsNullOrEmpty(compactSize) ? "" : $"  •  {compactSize}");
        OverviewPreviewMetaText.Text = current.FileName
            + (string.IsNullOrEmpty(compactRes) ? "" : $"  •  {compactRes}")
            + (string.IsNullOrEmpty(compactSize) ? "" : $"  •  {compactSize}");
        FilmstripCounterText.Text = $"{_currentIndex + 1} / {_items.Count} fotek • {totalSizeText}";
        FilmstripResolutionText.Text = current.FileName
            + (string.IsNullOrEmpty(compactRes) ? "" : $"  •  {compactRes}")
            + (string.IsNullOrEmpty(compactSize) ? "" : $"  •  {compactSize}");
        if (RightExifPanel.Visibility == Visibility.Visible)
        {
            UpdateRightExifPanel();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_isCropMode)
            {
                ExitCropMode();
            }
            else if (!_isOverviewMode && _items.Count > 0)
            {
                // Esc in Fullscreen returns to Overview (checking any pending rotation first)
                SwitchMode(overviewMode: true);
            }
            else
            {
                Close();
            }
            return;
        }

        if (e.Key == Key.O && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control))
        {
            e.Handled = true;
            ShowOpenFileDialog();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.C:
                    e.Handled = true;
                    ShowCopyFileDialog();
                    return;

                case Key.B:
                case Key.F3:
                    e.Handled = true;
                    ShowBatchConvertDialog();
                    return;

                case Key.M:
                    e.Handled = true;
                    ToggleMouseWheelMode();
                    return;

                case Key.U:
                    e.Handled = true;
                    ToggleEditSaveMode();
                    return;

                case Key.Delete:
                    e.Handled = true;
                    DeleteCurrentFileToRecycleBin();
                    return;

                case Key.L:
                    e.Handled = true;
                    _ = ApplyPhysicalTransformAsync(ImageTransformType.RotateLeft);
                    return;

                case Key.R:
                    e.Handled = true;
                    _ = ApplyPhysicalTransformAsync(ImageTransformType.RotateRight);
                    return;

                case Key.H:
                    e.Handled = true;
                    _ = ApplyPhysicalTransformAsync(ImageTransformType.FlipHorizontal);
                    return;

                case Key.V:
                    e.Handled = true;
                    _ = ApplyPhysicalTransformAsync(ImageTransformType.FlipVertical);
                    return;

                case Key.X:
                    if (!_isOverviewMode)
                    {
                        e.Handled = true;
                        if (_isCropMode)
                            ExitCropMode();
                        else
                            EnterCropMode();
                        return;
                    }
                    break;

                case Key.I:
                    if (!_isOverviewMode && !_isCropMode && _items.Count > 0)
                    {
                        e.Handled = true;
                        ToggleRightExifPanel();
                        return;
                    }
                    break;
            }
        }

        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            e.Handled = true;
            if (_isCropMode)
            {
                if (_hasCropSelection)
                {
                    ConfirmCurrentCropSelection();
                }
                else
                {
                    ExitCropMode();
                }
                return;
            }

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

        if (!_isOverviewMode && !_isCropMode)
        {
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
        if (_isOverviewMode || _isCropMode || FullscreenImage.Source == null || string.IsNullOrEmpty(e.Text))
            return;

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
        if (_isSortingItems)
            return;

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
        ExitCropMode();
        var dlg = new OpenFileDialog
        {
            Title = "Vyberte obrázek k prohlížení",
            Filter = ImageCacheService.OpenFileDialogFilter,
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

    // ==================== FOLDER TREE & LIVE PREVIEW (FASTSTONE OVERVIEW) ====================

    private void InitializeFolderTree()
    {
        _rootFolderNodes.Clear();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                string label = string.Empty;
                try
                {
                    label = drive.VolumeLabel;
                }
                catch
                {
                    // Ignore label error
                }

                string rootName = drive.Name.TrimEnd('\\');
                string display = string.IsNullOrWhiteSpace(label)
                    ? $"Disk ({rootName})"
                    : $"{label} ({rootName})";

                _rootFolderNodes.Add(new FolderNode(drive.RootDirectory.FullName, display, isDrive: true));
            }
        }
        catch
        {
            // Ignore drive enumeration errors
        }
    }

    private void SyncFolderTreeToPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        if (_rootFolderNodes.Count == 0)
        {
            InitializeFolderTree();
        }

        _isSyncingFolderTree = true;
        try
        {
            string fullPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? rootPath = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(rootPath))
                return;

            FolderNode? currentNode = _rootFolderNodes.FirstOrDefault(
                d => string.Equals(
                    d.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));

            if (currentNode == null)
                return;

            currentNode.IsExpanded = true;
            currentNode.EnsureChildrenLoaded();

            string relative = fullPath.Length > rootPath.Length
                ? fullPath[rootPath.Length..]
                : string.Empty;

            string[] segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string segment in segments)
            {
                FolderNode? next = currentNode.Children.FirstOrDefault(
                    c => string.Equals(c.DisplayName, segment, StringComparison.OrdinalIgnoreCase));

                if (next == null)
                {
                    currentNode.EnsureChildrenLoaded(forceRefresh: true);
                    next = currentNode.Children.FirstOrDefault(
                        c => string.Equals(c.DisplayName, segment, StringComparison.OrdinalIgnoreCase));
                }

                if (next == null)
                    break;

                currentNode = next;
                currentNode.IsExpanded = true;
                currentNode.EnsureChildrenLoaded();
            }

            if ( FolderTreeView.SelectedItem is FolderNode prevSelected && !ReferenceEquals(prevSelected, currentNode))
            {
                prevSelected.IsSelected = false;
            }

            currentNode.IsSelected = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                FolderTreeView.UpdateLayout();
                if (FindSelectedTreeViewItem(FolderTreeView) is TreeViewItem selectedTvi &&
                    selectedTvi.Template.FindName("HeaderBorder", selectedTvi) is FrameworkElement headerBorder)
                {
                    headerBorder.BringIntoView(new Rect(0, 0, Math.Min(headerBorder.ActualWidth, 160), headerBorder.ActualHeight));
                }
            }), DispatcherPriority.Loaded);
        }
        finally
        {
            _isSyncingFolderTree = false;
        }
    }

    private static TreeViewItem? FindSelectedTreeViewItem(ItemsControl parent)
    {
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem childTvi)
                continue;

            if (childTvi.IsSelected)
                return childTvi;

            if (childTvi.IsExpanded)
            {
                TreeViewItem? found = FindSelectedTreeViewItem(childTvi);
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null)
            return null;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            T? result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private void FolderTreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem tvi)
            return;

        if (e.OriginalSource is DependencyObject orig)
        {
            TreeViewItem? clickedItem = FindVisualAncestor<TreeViewItem>(orig);
            if (!ReferenceEquals(clickedItem, tvi))
                return;

            // Clicking the ▸ / ▾ expander toggle button only expands/collapses without changing selection
            if (FindVisualAncestor<System.Windows.Controls.Primitives.ToggleButton>(orig) != null)
                return;
        }

        e.Handled = true;
        tvi.IsSelected = true;
        tvi.Focus();

        // Single-click on a folder in the tree also expands its subfolders for quick navigation
        if (!tvi.IsExpanded)
        {
            tvi.IsExpanded = true;
        }
    }

    private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isSyncingFolderTree)
            return;

        if (e.NewValue is FolderNode node && !string.IsNullOrWhiteSpace(node.FullPath) && Directory.Exists(node.FullPath))
        {
            string selectedPath = Path.GetFullPath(node.FullPath);
            if (!string.Equals(selectedPath, _currentFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (!ResolvePendingTransformBeforeLeave())
                {
                    SyncFolderTreeToPath(_currentFolder);
                    return;
                }

                LoadFolder(selectedPath, selectedFilePath: null, startInFullscreen: false, syncTree: false, keepTreeFocus: true);
            }
        }
    }

    private void FolderTreeViewItem_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem tvi && ReferenceEquals(e.OriginalSource, tvi))
        {
            e.Handled = true;
            if (tvi.Template.FindName("HeaderBorder", tvi) is FrameworkElement headerBorder)
            {
                headerBorder.BringIntoView(new Rect(0, 0, Math.Min(headerBorder.ActualWidth, 160), headerBorder.ActualHeight));
            }
        }
    }

    private void RefreshFolderTreeButton_Click(object sender, RoutedEventArgs e)
    {
        InitializeFolderTree();
        if (!string.IsNullOrWhiteSpace(_currentFolder))
        {
            SyncFolderTreeToPath(_currentFolder);
            string? selectedFile = (_currentIndex >= 0 && _currentIndex < _items.Count)
                ? _items[_currentIndex].FilePath
                : null;
            LoadFolder(_currentFolder, selectedFile, startInFullscreen: false, syncTree: false);
        }
    }

    private void OverviewPreviewBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_items.Count > 0 && _currentIndex >= 0)
        {
            e.Handled = true;
            SwitchMode(overviewMode: false);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ResolvePendingTransformBeforeLeave())
        {
            e.Cancel = true;
            return;
        }

        if (OverviewLeftColumn.ActualWidth >= 180)
        {
            _settings.OverviewLeftPaneWidth = OverviewLeftColumn.ActualWidth;
        }
        if (OverviewPreviewRow.ActualHeight >= 100)
        {
            _settings.OverviewPreviewPaneHeight = OverviewPreviewRow.ActualHeight;
        }
        AppSettingsService.Save(_settings);
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
