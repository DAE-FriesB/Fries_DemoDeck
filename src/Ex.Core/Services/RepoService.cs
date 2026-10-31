using Ex.Core.Git;
using Ex.Core.Model;

namespace Ex.Core.Services;

public sealed record CommitInfo(string Id, string ShortId, string Subject, string Date);

public sealed record StashInfo(string Ref, string Message);

public sealed record TreeFile(string Path, long Size);

public enum ProjectType { Unity, DotNet }

/// <summary>Single-repository operations. Each method maps to a few plain git commands.</summary>
public sealed class RepoService(GitRunner git)
{
    public const string StashPrefix = "prep-autosave";

    private static readonly string[] GitSettingFiles = [".gitignore", ".gitattributes"];

    public GitRunner Git { get; } = git;

    public async Task<RepoState> ReadStateAsync(string path)
    {
        var head = await Git.QueryAsync(path, "symbolic-ref", "--short", "-q", "HEAD");
        var hasCommits = (await Git.QueryAsync(path, "rev-parse", "--verify", "-q", "HEAD")).Success;

        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        var refs = await Git.QueryAsync(path, "for-each-ref", "--format=%(refname:short)%09%(objectname)", "refs/heads");
        foreach (var line in refs.Lines)
        {
            var parts = line.Split('\t');
            if (parts.Length == 2)
                branches[parts[0]] = parts[1];
        }

        var status = await Git.QueryAsync(path, "status", "--porcelain=v1", "--untracked-files=all");
        var entries = status.Lines
            .Where(l => l.Length > 3)
            .Select(l => new StatusEntry(l[..2], l[3..]))
            .ToList();

        return new RepoState
        {
            Name = System.IO.Path.GetFileName(path),
            Path = path,
            CurrentBranch = head.Success ? head.StdOut.Trim() : null,
            IsEmpty = !hasCommits,
            Branches = branches,
            Status = entries,
        };
    }

    public async Task<bool> IsAncestorAsync(string path, string ancestor, string descendant) =>
        (await Git.QueryAsync(path, "merge-base", "--is-ancestor", ancestor, descendant)).ExitCode == 0;

    public async Task<string?> MergeBaseAsync(string path, string a, string b)
    {
        var result = await Git.QueryAsync(path, "merge-base", a, b);
        return result.Success ? result.StdOut.Trim() : null;
    }

    public async Task<int> CountCommitsAsync(string path, string range)
    {
        var result = await Git.QueryAsync(path, "rev-list", "--count", range);
        return result.Success && int.TryParse(result.StdOut.Trim(), out var n) ? n : 0;
    }

    public async Task<string?> ResolveAsync(string path, string rev)
    {
        var result = await Git.QueryAsync(path, "rev-parse", "--verify", "-q", rev + "^{commit}");
        return result.Success ? result.StdOut.Trim() : null;
    }

    // ---- cleaning the working state -------------------------------------------------------

    public async Task CommitAllAsync(string path, string message)
    {
        await Git.RunCheckedAsync(path, "add", "-A");
        await Git.RunCheckedAsync(path, "commit", "-m", message);
    }

    /// <summary>Stashes tracked and untracked changes under a recognisable name. Returns the stash message.</summary>
    public async Task<string> StashAllAsync(string path)
    {
        var message = $"{StashPrefix} {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        await Git.RunCheckedAsync(path, "stash", "push", "--include-untracked", "-m", message);
        return message;
    }

    /// <summary>Permanently discards tracked changes and untracked files. Ignored files (Library/, bin/ ...) are kept.</summary>
    public async Task DiscardAllAsync(string path)
    {
        if ((await Git.QueryAsync(path, "rev-parse", "--verify", "-q", "HEAD")).Success)
            await Git.RunCheckedAsync(path, "reset", "--hard", "-q");
        await Git.RunCheckedAsync(path, "clean", "-fd");
    }

    /// <summary>
    /// Permanently discards the given changes only: tracked files go back to the last commit, new files are deleted.
    /// </summary>
    public async Task DiscardFilesAsync(string path, IEnumerable<StatusEntry> entries)
    {
        var list = entries.ToList();
        var hasHead = (await Git.QueryAsync(path, "rev-parse", "--verify", "-q", "HEAD")).Success;
        var untracked = list.Where(e => e.IsUntracked || (!hasHead && e.Kind == ChangeKind.Added)).ToList();
        var tracked = list.Except(untracked).SelectMany(e => e.AllPaths).Distinct().ToList();

        if (!hasHead)
        {
            // Nothing committed yet: only unstage, then delete below.
            foreach (var chunk in untracked.Where(e => !e.IsUntracked).Select(e => e.Path).Chunk(100))
                await Git.RunCheckedAsync(path, ["rm", "--cached", "-q", "--", .. chunk]);
        }
        foreach (var chunk in tracked.Chunk(100))
            await Git.RunCheckedAsync(path, ["restore", "--source=HEAD", "--staged", "--worktree", "--", .. chunk]);
        foreach (var chunk in untracked.Select(e => e.Path).Chunk(100))
            await Git.RunCheckedAsync(path, ["clean", "-f", "-q", "--", .. chunk]);
    }

    /// <summary>
    /// The diff of one change against the last commit. New files are shown as entirely added.
    /// Returns null for binary files.
    /// </summary>
    public async Task<string?> DiffFileAsync(string path, StatusEntry entry, long maxBytes = 1024 * 1024)
    {
        var hasHead = (await Git.QueryAsync(path, "rev-parse", "--verify", "-q", "HEAD")).Success;
        if (entry.IsUntracked || (!hasHead && entry.Kind == ChangeKind.Added))
        {
            var full = System.IO.Path.Combine(path, entry.CurrentPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                return "";
            var info = new FileInfo(full);
            if (info.Length > maxBytes)
                return $"(new file, {info.Length / 1024.0 / 1024.0:0.0} MB: too large to show)";
            var bytes = await File.ReadAllBytesAsync(full);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
                return null;
            var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
            var lines = text.EndsWith('\n') ? text[..^1].Split('\n') : text.Split('\n');
            return $"@@ new file, {lines.Length} line{(lines.Length == 1 ? "" : "s")} @@\n" + string.Join("\n", lines.Select(l => "+" + l));
        }

        var diff = await Git.QueryAsync(path, ["diff", "--no-color", "--find-renames", "HEAD", "--", .. entry.AllPaths]);
        if (diff.StdOut.Contains("\nBinary files ") || diff.StdOut.StartsWith("Binary files "))
            return null;
        // Drop the header (diff --git, index, ---/+++); the window shows the path itself.
        var body = diff.StdOut.Replace("\r\n", "\n").Split('\n').SkipWhile(l => !l.StartsWith("@@"));
        return string.Join("\n", body).TrimEnd('\n');
    }

    public async Task<IReadOnlyList<StashInfo>> ListStashesAsync(string path)
    {
        var result = await Git.QueryAsync(path, "stash", "list", "--format=%gd%x09%gs");
        return result.Lines
            .Select(l => l.Split('\t', 2))
            .Where(p => p.Length == 2)
            .Select(p => new StashInfo(p[0], p[1]))
            .ToList();
    }

    public Task ApplyStashAsync(string path, string stashRef) => Git.RunCheckedAsync(path, "stash", "apply", stashRef);

    public Task DropStashAsync(string path, string stashRef) => Git.RunCheckedAsync(path, "stash", "drop", stashRef);

    // ---- branches -------------------------------------------------------------------------

    public Task SwitchAsync(string path, string branch) => Git.RunCheckedAsync(path, "switch", branch);

    public Task CreateBranchAsync(string path, string name, string startPoint) =>
        Git.RunCheckedAsync(path, "branch", name, startPoint);

    public Task DeleteBranchAsync(string path, string name) => Git.RunCheckedAsync(path, "branch", "-D", name);

    /// <summary>
    /// Creates <c>weekNN/start</c> and <c>weekNN/solution</c> on the same commit and checks out the solution.
    /// With <paramref name="source"/> null the week starts from an empty (orphan) commit that only carries
    /// <c>.gitignore</c> and <c>.gitattributes</c> over from <c>main</c>, so ignore rules are never lost.
    /// </summary>
    public async Task CreateWeekAsync(string path, int week, string? source)
    {
        var start = ExerciseBranch.NameFor(week, "start");
        var solution = ExerciseBranch.NameFor(week, "solution");
        if (source is null)
        {
            var hasMain = await ResolveAsync(path, "main") != null;
            await Git.RunCheckedAsync(path, "switch", "--orphan", start);
            if (hasMain)
            {
                foreach (var file in GitSettingFiles)
                {
                    if (await ReadFileAtAsync(path, "main", file) != null)
                        await Git.RunCheckedAsync(path, "checkout", "main", "--", file);
                }
            }
            await Git.RunCheckedAsync(path, "commit", "--allow-empty", "-m", $"{ExerciseBranch.WeekIdFor(week)}: empty start");
            await Git.RunCheckedAsync(path, "switch", "-c", solution);
        }
        else
        {
            await Git.RunCheckedAsync(path, "branch", start, source);
            await Git.RunCheckedAsync(path, "switch", "-c", solution, source);
        }
    }

    public async Task<IReadOnlyList<CommitInfo>> LogAsync(string path, string rev, int max = 50)
    {
        var result = await Git.QueryAsync(path, "log", $"-n{max}", "--format=%H%x09%h%x09%ad%x09%s", "--date=short", rev);
        return result.Lines
            .Select(l => l.Split('\t', 4))
            .Where(p => p.Length == 4)
            .Select(p => new CommitInfo(p[0], p[1], p[3], p[2]))
            .ToList();
    }

    public async Task<string> DiffAsync(string path, string from, string to)
    {
        var stat = await Git.QueryCheckedAsync(path, "diff", "--stat", from, to);
        var diff = await Git.QueryCheckedAsync(path, "diff", from, to);
        return stat.StdOut + "\n" + diff.StdOut;
    }

    /// <summary>
    /// Predicts the files that would conflict when merging <paramref name="from"/> into <paramref name="into"/>,
    /// entirely in memory (git merge-tree): nothing in the repo or working folder changes.
    /// </summary>
    public async Task<IReadOnlyList<string>> PredictConflictsAsync(string path, string into, string from)
    {
        var result = await Git.QueryAsync(path, "merge-tree", "--write-tree", "--name-only", "--no-messages", into, from);
        if (result.ExitCode == 0)
            return [];
        if (result.ExitCode != 1)
            throw new GitException($"git merge-tree failed: {result.StdErr.Trim()}", result);
        return result.Lines.Skip(1).Distinct().ToList(); // first line is the tree id
    }

    public async Task<IReadOnlyList<string>> UnmergedFilesAsync(string path)
    {
        var result = await Git.QueryAsync(path, "diff", "--name-only", "--diff-filter=U");
        return result.Lines;
    }

    // ---- content --------------------------------------------------------------------------

    public Task ExportZipAsync(string path, string branch, string zipFile) =>
        Git.RunCheckedAsync(path, "archive", "--format=zip", "-o", zipFile, branch);

    public async Task<string?> ReadFileAtAsync(string path, string rev, string file)
    {
        var result = await Git.QueryAsync(path, "show", $"{rev}:{file}");
        return result.Success ? result.StdOut : null;
    }

    public async Task<IReadOnlyList<TreeFile>> ListTreeAsync(string path, string rev)
    {
        var result = await Git.QueryAsync(path, "ls-tree", "-r", "-l", rev);
        var files = new List<TreeFile>();
        foreach (var line in result.Lines)
        {
            // <mode> SP <type> SP <object> SP+ <size> TAB <path>
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var meta = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length == 4 && long.TryParse(meta[3], out var size))
                files.Add(new TreeFile(line[(tab + 1)..], size));
        }
        return files;
    }

    // ---- new repositories -----------------------------------------------------------------

    public async Task<string> InitRepoAsync(string projectsPath, string name, ProjectType type)
    {
        var path = System.IO.Path.Combine(projectsPath, name);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException($"Folder {path} already exists and is not empty.");
        Directory.CreateDirectory(path);

        await Git.RunCheckedAsync(path, "init", "-b", "main");
        if (type == ProjectType.Unity)
            await Git.RunCheckedAsync(path, "config", "core.longpaths", "true");

        File.WriteAllText(System.IO.Path.Combine(path, ".gitignore"), Templates.GitIgnore(type));
        File.WriteAllText(System.IO.Path.Combine(path, ".gitattributes"), Templates.GitAttributes(type));
        File.WriteAllText(System.IO.Path.Combine(path, "README.md"), Templates.Readme(name, type));
        await CommitAllAsync(path, "Initial commit: README, .gitignore, .gitattributes");
        return path;
    }
}
