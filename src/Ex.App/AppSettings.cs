using System.IO;
using System.Text.Json;

namespace Ex.App;

/// <summary>Client settings kept in the user's app data (not in the course folder): recently opened course folders.</summary>
public sealed class AppSettings
{
    private const int MaxRecent = 10;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExercisesPipeline", "settings.json");

    public List<string> RecentFolders { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
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
