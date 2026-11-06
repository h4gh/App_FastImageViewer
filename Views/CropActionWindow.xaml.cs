using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FastImageViewer.Services;
using Microsoft.Win32;

namespace FastImageViewer.Views;

public enum CropResultAction
{
    None,
    CopiedToClipboard,
    SavedToFile
}

public partial class CropActionWindow : Window
{
    private readonly BitmapSource _croppedBitmap;
    private readonly string _sourceFilePath;
    private readonly bool _useSaveAsDialog;
    private readonly string _autoTargetFilePath;

    public CropResultAction ResultAction { get; private set; } = CropResultAction.None;
    public string? SavedFilePath { get; private set; }

    public CropActionWindow(BitmapSource croppedBitmap, string sourceFilePath, bool useSaveAsDialog)
    {
        InitializeComponent();
        _croppedBitmap = croppedBitmap;
        _sourceFilePath = sourceFilePath;
        _useSaveAsDialog = useSaveAsDialog;

        string dir = Path.GetDirectoryName(_sourceFilePath)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string baseName = Path.GetFileNameWithoutExtension(_sourceFilePath);
        string ext = ImageCacheService.NormalizeWritableExtension(Path.GetExtension(_sourceFilePath));

        string defaultFileName = GetUniqueCroppedFileName(dir, baseName, ext);
        _autoTargetFilePath = Path.Combine(dir, defaultFileName);

        PreviewImage.Source = _croppedBitmap;
        CropDimensionsText.Text = $"{_croppedBitmap.PixelWidth} × {_croppedBitmap.PixelHeight} px";

        if (_useSaveAsDialog)
        {
            TargetFileInfoText.Text = $"Režim „Uložit jako…“ — předvyplněný název: {defaultFileName}";
            SaveButtonText.Text = "Uložit jako… (Enter)";
        }
        else
        {
            TargetFileInfoText.Text = $"Při uložení se vytvoří: {defaultFileName}";
            SaveButtonText.Text = "Uložit _cropped (Enter)";
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (e.Key == Key.C)
        {
            e.Handled = true;
            CopyToClipboard();
        }
        else if (e.Key == Key.S)
        {
            e.Handled = true;
            SaveToNewFile();
        }
    }

    private void CopyToClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard();
    }

    private void SaveToFileButton_Click(object sender, RoutedEventArgs e)
    {
        SaveToNewFile();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = CropResultAction.None;
        DialogResult = false;
        Close();
    }

    private void CopyToClipboard()
    {
        try
        {
            Clipboard.SetImage(_croppedBitmap);
            ResultAction = CropResultAction.CopiedToClipboard;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Nelze zkopírovat do schránky: {ex.Message}");
        }
    }

    private void SaveToNewFile()
    {
        try
        {
            if (!_useSaveAsDialog)
            {
                ImageTransformService.SaveBitmapToFile(_croppedBitmap, _autoTargetFilePath, quality: 95);
                SavedFilePath = _autoTargetFilePath;
                ResultAction = CropResultAction.SavedToFile;
                DialogResult = true;
                Close();
                return;
            }

            string dir = Path.GetDirectoryName(_autoTargetFilePath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string defaultFileName = Path.GetFileName(_autoTargetFilePath);
            string ext = Path.GetExtension(_autoTargetFilePath).ToLowerInvariant();

            var dlg = new SaveFileDialog
            {
                Title = "Uložit výstřižek jako...",
                InitialDirectory = dir,
                FileName = defaultFileName,
                Filter = ImageCacheService.SaveFileDialogFilter,
                FilterIndex = ImageCacheService.GetSaveFilterIndex(ext)
            };

            if (dlg.ShowDialog(this) == true)
            {
                ImageTransformService.SaveBitmapToFile(_croppedBitmap, dlg.FileName, quality: 95);
                SavedFilePath = dlg.FileName;
                ResultAction = CropResultAction.SavedToFile;
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            ShowError($"Nelze uložit soubor: {ex.Message}");
        }
    }

    private static string GetUniqueCroppedFileName(string directory, string baseName, string ext)
    {
        string candidate = $"{baseName}_cropped{ext}";
        int counter = 2;
        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{baseName}_cropped_{counter}{ext}";
            counter++;
        }
        return candidate;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
