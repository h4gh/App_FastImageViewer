using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FastImageViewer.Views;

public enum SaveTransformChoice
{
    Cancel,
    SaveAsNewRotated,
    OverwriteOriginal,
    DiscardChanges
}

public partial class SaveTransformWindow : Window
{
    public SaveTransformChoice Choice { get; private set; } = SaveTransformChoice.Cancel;

    public SaveTransformWindow(string originalFilePath, string proposedNewFileName, bool useSaveAsDialog)
    {
        InitializeComponent();
        string origName = Path.GetFileName(originalFilePath);

        if (useSaveAsDialog)
        {
            InfoText.Text = $"Soubor '{origName}' byl upraven.\nPři volbě nové kopie se otevře dialog „Uložit jako…“ (výchozí: {proposedNewFileName}).";
            SaveNewButtonText.Text = "Uložit jako… (Enter)";
        }
        else
        {
            InfoText.Text = $"Soubor '{origName}' byl upraven.\nNová kopie bude automaticky uložena jako: {proposedNewFileName}";
            SaveNewButtonText.Text = "Jako novou _rotated (Enter)";
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.N:
                e.Handled = true;
                Choice = SaveTransformChoice.SaveAsNewRotated;
                DialogResult = true;
                Close();
                break;

            case Key.P:
                e.Handled = true;
                Choice = SaveTransformChoice.OverwriteOriginal;
                DialogResult = true;
                Close();
                break;

            case Key.D:
                e.Handled = true;
                Choice = SaveTransformChoice.DiscardChanges;
                DialogResult = true;
                Close();
                break;
        }
    }

    private void SaveAsNewButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = SaveTransformChoice.SaveAsNewRotated;
        DialogResult = true;
        Close();
    }

    private void OverwriteButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = SaveTransformChoice.OverwriteOriginal;
        DialogResult = true;
        Close();
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = SaveTransformChoice.DiscardChanges;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = SaveTransformChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
