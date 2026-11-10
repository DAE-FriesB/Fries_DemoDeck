using System.IO;
using System.Text.Json;

namespace DemoDeck.App;

/// <summary>Client settings kept in the user's app data (not in the course folder): recently opened course folders.</summary>
public sealed class AppSettings
{
    private const int MaxRecent = 10;

    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static readonly string FilePath = Path.Combine(AppData, "DemoDeck", "settings.json");

    /// <summary>Where versions before the rename to DemoDeck kept their settings.</summary>
    private static readonly string OldFilePath = Path.Combine(AppData, "ExercisesPipeline", "settings.json");

    public List<string> RecentFolders { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            // First start after the rename: bring the recent course folders along.
            if (!File.Exists(FilePath) && File.Exists(OldFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(OldFilePath, FilePath);
            }
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A broken settings file only loses the recent list.
        }
        return new AppSettings();
    }

    public void AddRecent(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > MaxRecent)
            RecentFolders.RemoveRange(MaxRecent, RecentFolders.Count - MaxRecent);
        Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
