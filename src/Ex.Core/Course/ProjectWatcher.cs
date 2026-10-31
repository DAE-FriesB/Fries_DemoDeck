namespace Ex.Core.Course;

/// <summary>
/// Watches the projects folder and raises <see cref="Changed"/> once things settle down (debounced), so the client
/// can refresh like a git client does. Noise is filtered out: Unity's Library/Temp, build output, IDE folders and
/// git internals other than the branch pointers (HEAD, refs), so the client's own status queries never trigger it.
/// </summary>
public sealed class ProjectWatcher : IDisposable
{
    private static readonly HashSet<string> IgnoredRepoRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "Temp", "Logs", "UserSettings", "Build", "Builds", "MemoryCaptures", "Recordings",
    };

    private static readonly HashSet<string> IgnoredFoldersAnywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".vs", ".idea", ".vscode",
    };

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly TimeSpan _delay;

    public ProjectWatcher(string projectsPath, TimeSpan? delay = null)
    {
        _delay = delay ?? TimeSpan.FromMilliseconds(800);
        _debounce = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        Directory.CreateDirectory(projectsPath);
        _watcher = new FileSystemWatcher(projectsPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += (s, e) => { if (IsRelevant(e.OldName) || IsRelevant(e.Name)) Poke(); };
        _watcher.Error += (_, _) => Poke(); // buffer overflow: we missed events, so refresh anyway
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Raised on a thread-pool thread, at most once per quiet period.</summary>
    public event Action? Changed;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        // A repo folder itself reports "changed" whenever something inside it changes (even in Library/);
        // at that level only additions and removals matter.
        var isRepoFolder = e.Name?.IndexOfAny(['\\', '/']) < 0;
        if (isRepoFolder && e.ChangeType == WatcherChangeTypes.Changed)
            return;
        if (IsRelevant(e.Name))
            Poke();
    }

    private void Poke() => _debounce.Change(_delay, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Decides whether a change matters. <paramref name="relativePath"/> is relative to the projects folder,
    /// so its first segment is the repo folder.
    /// </summary>
    public static bool IsRelevant(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;
        var parts = relativePath.Split('\\', '/');
        if (parts.Length < 2)
            return true; // a repo folder was added or removed

        var inRepo = parts.AsSpan(1);
        if (inRepo[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            // Only branch pointers: a switch, commit or new branch made outside the client.
            if (inRepo.Length < 2) return false;
            var name = inRepo[1];
            return name.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("packed-refs", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("refs", StringComparison.OrdinalIgnoreCase);
        }
        if (IgnoredRepoRootFolders.Contains(inRepo[0]))
            return false;
        foreach (var part in inRepo)
        {
            if (IgnoredFoldersAnywhere.Contains(part))
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
