using System.Text.RegularExpressions;
using Ex.Core.Course;
using Ex.Core.Model;

namespace Ex.Core.Services;

public enum IssueSeverity { Info, Warning, Error }

public sealed record CheckIssue(IssueSeverity Severity, string? Repo, string Message)
{
    public override string ToString() => $"[{Severity}] {(Repo != null ? Repo + ": " : "")}{Message}";
}

/// <summary>
/// Validates naming, snapshot positions, Unity versions, large files and the YAML against the branches.
/// Only reports; never changes anything.
/// </summary>
public sealed partial class CheckService(RepoService repos)
{
    private const string UnityVersionFile = "ProjectSettings/ProjectVersion.txt";

    public async Task<List<CheckIssue>> RunAsync(CourseManifest manifest, IReadOnlyList<RepoState> states)
    {
        var issues = new List<CheckIssue>();
        foreach (var repo in states)
        {
            CheckNaming(repo, issues);
            await CheckWeekStructureAsync(repo, issues);
            await CheckUnityVersionsAsync(repo, manifest.Settings.UnityBaseline, issues);
            await CheckLargeFilesAsync(repo, manifest.Settings.LargeFileMb, issues);
        }
        CheckManifest(manifest, states, issues);
        return issues;
    }

    private static void CheckNaming(RepoState repo, List<CheckIssue> issues)
    {
        foreach (var name in repo.Branches.Keys.Where(b => !BranchNames.IsReserved(b)))
        {
            if (BareWeekRegex().IsMatch(name))
                issues.Add(new(IssueSeverity.Error, repo.Name, $"bare branch '{name}': use {name}/start, {name}/solution ..."));
            else if (name.StartsWith("week", StringComparison.OrdinalIgnoreCase) && !ExerciseBranch.TryParse(name, out _))
                issues.Add(new(IssueSeverity.Warning, repo.Name, $"'{name}' does not follow weekNN/start | step-N | solution"));
        }
    }

    private async Task CheckWeekStructureAsync(RepoState repo, List<CheckIssue> issues)
    {
        foreach (var week in repo.ExerciseBranches.GroupBy(b => b.Week))
        {
            var list = week.Order().ToList();
            var id = ExerciseBranch.WeekIdFor(week.Key);
            if (list[0].Kind != BranchKind.Start)
                issues.Add(new(IssueSeverity.Warning, repo.Name, $"{id} has no start branch"));
            if (list[^1].Kind != BranchKind.Solution)
                issues.Add(new(IssueSeverity.Warning, repo.Name, $"{id} has no solution branch"));

            // Every entry should contain the previous one: start, step-1, step-2 ... solution form one line.
            for (var i = 1; i < list.Count; i++)
            {
                if (!await repos.IsAncestorAsync(repo.Path, list[i - 1].Name, list[i].Name))
                    issues.Add(new(IssueSeverity.Warning, repo.Name, $"{list[i].Name} does not contain {list[i - 1].Name} (snapshot out of line, or a fix that was not propagated)"));
            }
        }
    }

    private async Task CheckUnityVersionsAsync(RepoState repo, string baseline, List<CheckIssue> issues)
    {
        var expectedPrefix = UnityVersionPrefix(baseline);
        var versions = new Dictionary<string, List<string>>();
        foreach (var branch in repo.ExerciseBranches)
        {
            var text = await repos.ReadFileAtAsync(repo.Path, branch.Name, UnityVersionFile);
            if (text == null) continue;
            var version = ParseUnityVersion(text) ?? "(unreadable)";
            if (!versions.TryGetValue(version, out var names))
                versions[version] = names = [];
            names.Add(branch.Name);
        }
        if (versions.Count == 0) return;

        foreach (var (version, names) in versions)
        {
            if (!version.StartsWith(expectedPrefix, StringComparison.Ordinal))
                issues.Add(new(IssueSeverity.Warning, repo.Name, $"Unity {version} differs from the baseline {baseline} on: {string.Join(", ", names)}"));
        }
        if (versions.Count > 1)
            issues.Add(new(IssueSeverity.Warning, repo.Name, $"branches use {versions.Count} Unity versions: {string.Join(", ", versions.Keys)}"));
    }

    private async Task CheckLargeFilesAsync(RepoState repo, int limitMb, List<CheckIssue> issues)
    {
        var limit = limitMb * 1024L * 1024L;
        var reported = new HashSet<string>();
        foreach (var branch in repo.Branches.Keys.Where(b => !BranchNames.IsReserved(b)))
        {
            foreach (var file in await repos.ListTreeAsync(repo.Path, branch))
            {
                if (file.Size > limit && reported.Add(file.Path))
                    issues.Add(new(IssueSeverity.Warning, repo.Name, $"large file {file.Path} ({file.Size / 1024.0 / 1024.0:0.0} MB, limit {limitMb} MB), first seen on {branch}"));
            }
        }
    }

    private static void CheckManifest(CourseManifest manifest, IReadOnlyList<RepoState> states, List<CheckIssue> issues)
    {
        var repoNames = states.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var week in manifest.Weeks)
        {
            if (!ExerciseBranch.TryParseWeekId(week.Id, out var number))
            {
                issues.Add(new(IssueSeverity.Warning, null, $"YAML week id '{week.Id}' is not of the form weekNN"));
                continue;
            }
            var withWeek = states.Where(s => s.ExerciseBranches.Any(b => b.Week == number)).ToList();
            if (withWeek.Count == 0)
                issues.Add(new(IssueSeverity.Warning, null, $"YAML entry {week.Id} has no branch in any repo"));

            foreach (var repo in week.Repos)
            {
                if (!repoNames.Contains(repo))
                    issues.Add(new(IssueSeverity.Warning, null, $"{week.Id} lists repo '{repo}', which is not in the projects folder"));
                else if (!withWeek.Any(s => string.Equals(s.Name, repo, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new(IssueSeverity.Warning, repo, $"{week.Id} is meant to use this repo, but it has no {week.Id}/... branches"));
            }

            foreach (var step in week.Steps)
            {
                var branch = ExerciseBranch.NameFor(number, step.Id);
                if (!states.Any(s => s.HasBranch(branch)))
                    issues.Add(new(IssueSeverity.Warning, null, $"YAML step {branch} has no branch in any repo"));
            }
        }
    }

    /// <summary>Unity 6.x editor versions are numbered 6000.x; "6.6" therefore means "6000.6.".</summary>
    public static string UnityVersionPrefix(string baseline)
    {
        var m = Regex.Match(baseline.Trim(), @"^6\.(\d+)$");
        return m.Success ? $"6000.{m.Groups[1].Value}." : baseline.Trim();
    }

    public static string? ParseUnityVersion(string projectVersionTxt)
    {
        var m = Regex.Match(projectVersionTxt, @"m_EditorVersion:\s*(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^week\d+$")]
    private static partial Regex BareWeekRegex();
}
