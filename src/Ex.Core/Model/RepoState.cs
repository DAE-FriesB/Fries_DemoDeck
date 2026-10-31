namespace Ex.Core.Model;

/// <summary>One line of <c>git status --porcelain</c>: the two-letter code and the path.</summary>
public enum ChangeKind { Modified, Added, Deleted, Renamed, Untracked }

public sealed record StatusEntry(string Code, string Path)
{
    public bool IsUntracked => Code == "??";

    public ChangeKind Kind =>
        IsUntracked ? ChangeKind.Untracked
        : Code.Contains('D') ? ChangeKind.Deleted
        : Code.Contains('R') ? ChangeKind.Renamed
        : Code.Contains('A') ? ChangeKind.Added
        : ChangeKind.Modified;

    /// <summary>The path in the working folder (for a rename, the new path).</summary>
    public string CurrentPath => Kind == ChangeKind.Renamed && Path.Contains(" -> ") ? Path[(Path.IndexOf(" -> ") + 4)..] : Path;

    /// <summary>Every path git has to touch to undo this change (both sides of a rename).</summary>
    public IEnumerable<string> AllPaths => Kind == ChangeKind.Renamed && Path.Contains(" -> ")
        ? Path.Split(" -> ")
        : [Path];

    public override string ToString() => $"{Code} {Path}";
}

/// <summary>A snapshot of one exercise repository, as read from git.</summary>
public sealed class RepoState
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    /// <summary>The checked-out branch, or null for a detached HEAD.</summary>
    public string? CurrentBranch { get; init; }

    /// <summary>True when the repository has no commits yet.</summary>
    public bool IsEmpty { get; init; }

    /// <summary>All local branches, name to commit id.</summary>
    public IReadOnlyDictionary<string, string> Branches { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<StatusEntry> Status { get; init; } = [];

    public bool IsDirty => Status.Count > 0;

    public bool IsUnityProject => File.Exists(System.IO.Path.Combine(Path, "ProjectSettings", "ProjectVersion.txt"));

    /// <summary>Unity writes this lock file while the project is open in the Editor.</summary>
    public bool IsUnityOpen => File.Exists(System.IO.Path.Combine(Path, "Temp", "UnityLockfile"));

    public bool IsOnDemoBranch => BranchNames.IsDemo(CurrentBranch);

    public IEnumerable<ExerciseBranch> ExerciseBranches =>
        Branches.Keys
            .Select(b => ExerciseBranch.TryParse(b, out var eb) ? eb : null)
            .OfType<ExerciseBranch>()
            .Order();

    public bool HasBranch(string name) => Branches.ContainsKey(name);
}
