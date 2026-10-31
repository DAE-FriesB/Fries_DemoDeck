using Ex.Core.Course;
using Ex.Core.Model;

namespace Ex.Core.Services;

/// <summary>One entry of a week (start, step-N or solution), combined over all repos that have it.</summary>
public sealed class EntryOverview
{
    public required string WeekId { get; init; }
    public required string EntryId { get; init; }
    public required string BranchName { get; init; }
    public required int Order { get; init; }
    public List<string> Repos { get; } = [];
    public string? Title { get; set; }

    /// <summary>
    /// Saves on this entry that the entry after it does not have yet (summed over repos): made after a later step
    /// was created, so they still have to be propagated.
    /// </summary>
    public int Unsynced { get; set; }

    public string DisplayName => Title is { Length: > 0 } ? $"{EntryId}  -  {Title}" : EntryId;
}

/// <summary>A week combined over all repos: branches come from git, titles and notes from the YAML.</summary>
public sealed class WeekOverview
{
    public required int Week { get; init; }
    public required string Id { get; init; }
    public WeekEntry? Manifest { get; set; }
    public List<EntryOverview> Entries { get; } = [];

    /// <summary>Per repo: true when <c>start</c> descends from the previous week's solution, false for a fresh start.</summary>
    public Dictionary<string, bool> Continuation { get; } = [];

    /// <summary>True when the YAML mentions this week but no repo has a branch for it.</summary>
    public bool OnlyInManifest => Entries.Count == 0;

    public string? Title => Manifest?.Title;

    public string DisplayName => Title is { Length: > 0 } ? $"{Id}  -  {Title}" : Id;

    public string ContinuationText
    {
        get
        {
            if (Continuation.Count == 0) return "";
            return string.Join(", ", Continuation.Select(kv => $"{kv.Key}: {(kv.Value ? "continues previous week" : "fresh start")}"));
        }
    }
}

public sealed class OverviewBuilder(RepoService repos)
{
    public async Task<List<WeekOverview>> BuildAsync(CourseManifest manifest, IReadOnlyList<RepoState> states)
    {
        var weeks = new SortedDictionary<int, WeekOverview>();

        WeekOverview GetWeek(int number)
        {
            if (!weeks.TryGetValue(number, out var w))
            {
                var id = ExerciseBranch.WeekIdFor(number);
                w = new WeekOverview { Week = number, Id = id, Manifest = manifest.FindWeek(id) };
                weeks[number] = w;
            }
            return w;
        }

        foreach (var repo in states)
        {
            foreach (var branch in repo.ExerciseBranches)
            {
                var week = GetWeek(branch.Week);
                var entry = week.Entries.FirstOrDefault(e => e.EntryId == branch.EntryId);
                if (entry == null)
                {
                    entry = new EntryOverview
                    {
                        WeekId = week.Id,
                        EntryId = branch.EntryId,
                        BranchName = branch.Name,
                        Order = branch.Order,
                        Title = week.Manifest?.FindStep(branch.EntryId)?.Title,
                    };
                    week.Entries.Add(entry);
                }
                entry.Repos.Add(repo.Name);
            }

            await AddContinuationAsync(repo, weeks);
            await AddUnsyncedAsync(repo, weeks);
        }

        foreach (var entry in manifest.Weeks)
        {
            if (ExerciseBranch.TryParseWeekId(entry.Id, out var number))
                GetWeek(number);
        }

        foreach (var week in weeks.Values)
            week.Entries.Sort((a, b) => a.Order.CompareTo(b.Order));

        return weeks.Values.ToList();
    }

    /// <summary>
    /// A week continues the previous one when the previous week's start is in its history. (Checking the previous
    /// solution itself would break as soon as that solution gets another save.)
    /// </summary>
    private async Task AddContinuationAsync(RepoState repo, SortedDictionary<int, WeekOverview> weeks)
    {
        var branches = repo.ExerciseBranches.ToList();
        foreach (var start in branches.Where(b => b.Kind == BranchKind.Start))
        {
            var previousWeek = branches.Where(b => b.Week < start.Week).Select(b => b.Week).DefaultIfEmpty(0).Max();
            var previousStart = branches.FirstOrDefault(b => b.Week == previousWeek && b.Kind == BranchKind.Start)
                                ?? branches.Where(b => b.Week == previousWeek).MinBy(b => b.Order);
            var continues = previousStart != null
                && await repos.IsAncestorAsync(repo.Path, previousStart.Name, start.Name);
            weeks[start.Week].Continuation[repo.Name] = continues;
        }
    }

    /// <summary>
    /// For every entry, the number of its saves that the entry after it lacks. "After" is the next entry of the same
    /// week; for a solution, the next week's start when that week continues from it.
    /// </summary>
    private async Task AddUnsyncedAsync(RepoState repo, SortedDictionary<int, WeekOverview> weeks)
    {
        var branches = repo.ExerciseBranches.ToList();
        for (var i = 0; i < branches.Count - 1; i++)
        {
            var branch = branches[i];
            var next = branches[i + 1];
            if (next.Week != branch.Week)
            {
                if (next.Kind != BranchKind.Start || !weeks[next.Week].Continuation.GetValueOrDefault(repo.Name))
                    continue;
            }
            var count = await repos.CountCommitsAsync(repo.Path, $"{next.Name}..{branch.Name}");
            if (count > 0 && weeks[branch.Week].Entries.FirstOrDefault(e => e.BranchName == branch.Name) is { } entry)
                entry.Unsynced += count;
        }
    }
}
