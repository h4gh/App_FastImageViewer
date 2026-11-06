using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastImageViewer.Services;
using Microsoft.Win32;

namespace FastImageViewer.Views;

public sealed class BatchFileEntry
{
    public string FullPath { get; }
    public string FileName { get; }
    public string SizeText { get; }

    public BatchFileEntry(string fullPath)
    {
        FullPath = Path.GetFullPath(fullPath);
        FileName = Path.GetFileName(FullPath);

        try
        {
            var fi = new FileInfo(FullPath);
            if (fi.Exists)
            {
                double kb = fi.Length / 1024.0;
                SizeText = kb >= 1024.0 ? $"{kb / 1024.0:F2} MB" : $"{kb:F0} KB";
            }
            else
            {
                SizeText = string.Empty;
            }
        }
        catch
        {
            SizeText = string.Empty;
        }
    }
}

public partial class BatchConvertWindow : Window
{
    private readonly ObservableCollection<BatchFileEntry> _files = [];
    private readonly HashSet<string> _fileSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _currentViewerFolder;
    private CancellationTokenSource? _conversionCts;
    private bool _isRunning;

    public string? LastUsedOutputFolder { get; private set; }
    public bool AnyConvertedInCurrentFolder { get; private set; }

    public BatchConvertWindow(
        IEnumerable<string> initialFiles,
        string currentViewerFolder,
        string? savedOutputFolder)
    {
        InitializeComponent();
        _currentViewerFolder = currentViewerFolder;
        FilesListBox.ItemsSource = _files;

        string defaultOutDir = !string.IsNullOrWhiteSpace(savedOutputFolder)
            ? savedOutputFolder!
            : (!string.IsNullOrWhiteSpace(currentViewerFolder)
                ? Path.Combine(currentViewerFolder, "Converted")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Converted"));

        OutputFolderTextBox.Text = defaultOutDir;

        PopulateResizePresets();
        AddFiles(initialFiles);
    }

    private void PopulateResizePresets()
    {
        if (ResizePresetComboBox == null || ResizeModeComboBox == null)
            return;

        ResizePresetComboBox.Items.Clear();

        if (ResizeModeComboBox.SelectedIndex == 0)
        {
            // Delší strana (px)
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "3840 px (4K UHD)", Tag = 3840 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "2560 px (2K / QHD)", Tag = 2560 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "1920 px (Full HD)", Tag = 1920 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "1600 px", Tag = 1600 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "1280 px (HD)", Tag = 1280 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "1024 px (Web)", Tag = 1024 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "800 px (Malý náhled)", Tag = 800 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "Vlastní hodnota (px)…", Tag = -1 });

            ResizePresetComboBox.SelectedIndex = 2; // 1920 px default
            if (CustomResizeUnitText != null) CustomResizeUnitText.Text = "px";
            if (CustomResizeValueTextBox != null) CustomResizeValueTextBox.Text = "1920";
        }
        else
        {
            // Procenta (%)
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "80 %", Tag = 80 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "60 %", Tag = 60 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "50 % (polovina)", Tag = 50 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "40 %", Tag = 40 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "20 %", Tag = 20 });
            ResizePresetComboBox.Items.Add(new ComboBoxItem { Content = "Vlastní hodnota (%)…", Tag = -1 });

            ResizePresetComboBox.SelectedIndex = 1; // 60 % default
            if (CustomResizeUnitText != null) CustomResizeUnitText.Text = "%";
            if (CustomResizeValueTextBox != null) CustomResizeValueTextBox.Text = "60";
        }

        UpdateCustomResizeVisibility();
    }

    private void UpdateCustomResizeVisibility()
    {
        if (CustomResizeValueGrid == null || ResizePresetComboBox == null)
            return;

        if (ResizePresetComboBox.SelectedItem is ComboBoxItem item && item.Tag is int val && val == -1)
        {
            CustomResizeValueGrid.Visibility = Visibility.Visible;
            CustomResizeValueTextBox.Focus();
            CustomResizeValueTextBox.SelectAll();
        }
        else
        {
            CustomResizeValueGrid.Visibility = Visibility.Collapsed;
        }
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (string p in paths)
        {
            if (string.IsNullOrWhiteSpace(p))
                continue;

            if (File.Exists(p) && ImageCacheService.IsSupportedFile(p))
            {
                string full = Path.GetFullPath(p);
                if (_fileSet.Add(full))
                {
                    _files.Add(new BatchFileEntry(full));
                }
            }
            else if (Directory.Exists(p))
            {
                foreach (string img in ImageCacheService.GetImagesInFolder(p))
                {
                    string full = Path.GetFullPath(img);
                    if (_fileSet.Add(full))
                    {
                        _files.Add(new BatchFileEntry(full));
                    }
                }
            }
        }

        UpdateFilesCountUI();
    }

    private void UpdateFilesCountUI()
    {
        InputFilesHeaderText.Text = $"VSTUPNÍ SOUBORY ({_files.Count})";
        EmptyFilesHintText.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_isRunning)
        {
            ProgressStatusText.Text = _files.Count > 0
                ? $"Připraveno ke konverzi ({_files.Count} souborů)"
                : "Přidejte soubory ke konverzi";
            ProgressPercentText.Text = "0 %";
            ConversionProgressBar.Value = 0;
        }
    }

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        var dlg = new OpenFileDialog
        {
            Title = "Vyberte obrázky pro dávkovou konverzi",
            Multiselect = true,
            Filter = ImageCacheService.OpenFileDialogFilter,
            InitialDirectory = Directory.Exists(_currentViewerFolder)
                ? _currentViewerFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dlg.ShowDialog(this) == true)
        {
            AddFiles(dlg.FileNames);
        }
    }

    private void AddCurrentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        if (!string.IsNullOrWhiteSpace(_currentViewerFolder) && Directory.Exists(_currentViewerFolder))
        {
            AddFiles(ImageCacheService.GetImagesInFolder(_currentViewerFolder));
        }
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        var selected = FilesListBox.SelectedItems.Cast<BatchFileEntry>().ToList();
        foreach (var item in selected)
        {
            _files.Remove(item);
            _fileSet.Remove(item.FullPath);
        }
        UpdateFilesCountUI();
    }

    private void ClearFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        _files.Clear();
        _fileSet.Clear();
        UpdateFilesCountUI();
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityPanel == null)
            return;

        bool hasQualitySlider = FormatComboBox.SelectedIndex is 0 or 1;
        QualityPanel.IsEnabled = hasQualitySlider;
        QualityPanel.Opacity = hasQualitySlider ? 1.0 : 0.4;
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityValueText != null)
        {
            QualityValueText.Text = $"{(int)Math.Round(QualitySlider.Value)} %";
        }
    }

    private void EnableResizeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ResizeSettingsPanel == null)
            return;

        bool enabled = EnableResizeCheckBox.IsChecked == true;
        ResizeSettingsPanel.IsEnabled = enabled;
        ResizeSettingsPanel.Opacity = enabled ? 1.0 : 0.45;
    }

    private void ResizeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateResizePresets();
    }

    private void ResizePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomResizeVisibility();
    }

    private void BrowseOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        string currentOut = OutputFolderTextBox.Text.Trim();
        var dlg = new OpenFolderDialog
        {
            Title = "Vyberte výstupní složku pro zkonvertované obrázky",
            InitialDirectory = Directory.Exists(currentOut)
                ? currentOut
                : (Directory.Exists(_currentViewerFolder) ? _currentViewerFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
        };

        if (dlg.ShowDialog(this) == true)
        {
            OutputFolderTextBox.Text = dlg.FolderName;
        }
    }

    private async void StartConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _conversionCts?.Cancel();
            return;
        }

        if (_files.Count == 0)
        {
            MessageBox.Show(this, "Seznam vstupních souborů je prázdný.", "Dávková konverze", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string outFolder = OutputFolderTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(outFolder))
        {
            MessageBox.Show(this, "Zadejte prosím platnou výstupní složku.", "Dávková konverze", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BatchOutputFormat format = FormatComboBox.SelectedIndex switch
        {
            1 => BatchOutputFormat.Webp,
            2 => BatchOutputFormat.Png,
            3 => BatchOutputFormat.Bmp,
            4 => BatchOutputFormat.Tiff,
            _ => BatchOutputFormat.Jpeg
        };

        int quality = (int)Math.Round(QualitySlider.Value);
        bool enableResize = EnableResizeCheckBox.IsChecked == true;
        BatchResizeMode resizeMode = ResizeModeComboBox.SelectedIndex == 1
            ? BatchResizeMode.Percentage
            : BatchResizeMode.LongEdgePixels;

        int resizeValue = 1920;
        if (enableResize)
        {
            if (ResizePresetComboBox.SelectedItem is ComboBoxItem presetItem && presetItem.Tag is int presetTag)
            {
                if (presetTag > 0)
                {
                    resizeValue = presetTag;
                }
                else
                {
                    if (!int.TryParse(CustomResizeValueTextBox.Text.Trim(), out int parsedCustom) || parsedCustom <= 0)
                    {
                        MessageBox.Show(this, "Zadejte prosím platné kladné číslo pro vlastní rozměr.", "Neplatný rozměr", MessageBoxButton.OK, MessageBoxImage.Warning);
                        CustomResizeValueTextBox.Focus();
                        return;
                    }
                    resizeValue = parsedCustom;
                }
            }
        }

        var options = new BatchConvertOptions(format, quality, enableResize, resizeMode, resizeValue, outFolder);
        var inputList = _files.Select(f => f.FullPath).ToList();

        _isRunning = true;
        _conversionCts = new CancellationTokenSource();
        StartConvertButtonText.Text = "⏹ Zastavit";
        ConversionProgressBar.Value = 0;
        ProgressPercentText.Text = "0 %";
        ProgressStatusText.Text = "Spouštím konverzi...";

        var progress = new Progress<BatchProgressReport>(report =>
        {
            double pct = report.TotalCount > 0 ? (report.CompletedCount * 100.0 / report.TotalCount) : 0;
            ConversionProgressBar.Value = pct;
            ProgressPercentText.Text = $"{(int)Math.Round(pct)} %";
            ProgressStatusText.Text = $"Zpracovává se {report.CompletedCount} / {report.TotalCount}   —   {report.CurrentFileName}";
        });

        try
        {
            var (successCount, errorCount) = await BatchConversionService.RunBatchAsync(
                inputList,
                options,
                progress,
                _conversionCts.Token);

            LastUsedOutputFolder = Path.GetFullPath(outFolder);
            if (string.Equals(LastUsedOutputFolder, _currentViewerFolder, StringComparison.OrdinalIgnoreCase))
            {
                AnyConvertedInCurrentFolder = true;
            }

            ConversionProgressBar.Value = 100;
            ProgressPercentText.Text = "100 %";
            ProgressStatusText.Text = errorCount == 0
                ? $"✓ Hotovo! Úspěšně zkonvertováno {successCount} / {inputList.Count} souborů do '{LastUsedOutputFolder}'."
                : $"Dokončeno: {successCount} úspěšně, {errorCount} chyb.";
        }
        catch (OperationCanceledException)
        {
            ProgressStatusText.Text = "⏹ Konverze byla přerušena uživatelem.";
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = $"Chyba: {ex.Message}";
        }
        finally
        {
            _isRunning = false;
            StartConvertButtonText.Text = "▶ Spustit konverzi";
            _conversionCts?.Dispose();
            _conversionCts = null;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _conversionCts?.Cancel();
        }
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_isRunning)
            {
                _conversionCts?.Cancel();
            }
            else
            {
                Close();
            }
            return;
        }

        if (e.Key == Key.Delete && FilesListBox.IsKeyboardFocusWithin && !_isRunning)
        {
            e.Handled = true;
            RemoveSelectedButton_Click(sender, e);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!_isRunning && e.Data.GetDataPresent(DataFormats.FileDrop))
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
        if (_isRunning) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
        {
            AddFiles(dropped);
        }
    }
}
