using System.IO;
using System.Windows;
using FastImageViewer.Services;
using Microsoft.Win32;

namespace FastImageViewer.Views;

public partial class CopyFileWindow : Window
{
    private readonly string _sourceFilePath;

    public string? SelectedTargetFolder { get; private set; }

    public CopyFileWindow(string sourceFilePath, string initialTargetFolder)
    {
        InitializeComponent();
        _sourceFilePath = sourceFilePath;
        SourceFileNameText.Text = $"Soubor: {Path.GetFileName(sourceFilePath)}";
        TargetFolderTextBox.Text = initialTargetFolder;

        Loaded += (_, _) =>
        {
            TargetFolderTextBox.Focus();
            TargetFolderTextBox.SelectAll();
        };
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        string currentPath = TargetFolderTextBox.Text.Trim();
        var dlg = new OpenFolderDialog
        {
            Title = "Vyberte cílovou složku pro zkopírování souboru",
            InitialDirectory = Directory.Exists(currentPath)
                ? currentPath
                : Path.GetDirectoryName(_sourceFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dlg.ShowDialog(this) == true)
        {
            TargetFolderTextBox.Text = dlg.FolderName;
            TargetFolderTextBox.Focus();
            TargetFolderTextBox.SelectAll();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        string targetFolder = TargetFolderTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            ShowError("Zadejte prosím platnou cílovou složku.");
            return;
        }

        try
        {
            string fullTargetFolder = Path.GetFullPath(targetFolder);
            string destFile = Path.Combine(fullTargetFolder, Path.GetFileName(_sourceFilePath));

            if (string.Equals(Path.GetFullPath(_sourceFilePath), destFile, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Cílová složka je shodná se zdrojovou složkou.");
                return;
            }

            if (File.Exists(destFile))
            {
                var overwriteResult = MessageBox.Show(
                    this,
                    $"Soubor '{Path.GetFileName(_sourceFilePath)}' již v cílové složce existuje.\nChcete jej přepsat?",
                    "Přepsat soubor?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (overwriteResult != MessageBoxResult.Yes)
                    return;
            }

            FileOperationsService.CopyFileToFolder(_sourceFilePath, fullTargetFolder, overwrite: true);
            SelectedTargetFolder = fullTargetFolder;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Chyba při kopírování: {ex.Message}");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
