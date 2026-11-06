using System.IO;
using System.Text.Json;

namespace FastImageViewer.Services;

public sealed class AppSettings
{
    public string? LastFolderPath { get; set; }
    public string? LastCopyTargetFolder { get; set; }
    public string? LastBatchOutputFolder { get; set; }
    public bool MouseWheelZoom { get; set; } = false;
    public bool UseSaveAsDialogForEdits { get; set; } = false;
    public double OverviewLeftPaneWidth { get; set; } = 285;
    public double OverviewPreviewPaneHeight { get; set; } = 235;
    public string? LastRunVersion { get; set; }
}

public static class AppSettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FastImageViewer");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Ignore corrupted settings and return default
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
