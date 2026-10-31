using Ex.Core.Course;
using Ex.Core.Model;

namespace Ex.Core.Services;

public sealed record RepoOutcome(string Repo, bool Success, string Message);

/// <summary>Result of a multi-repo switch. <see cref="Rescued"/> lists rescue branches created on the way.</summary>
public sealed class SwitchReport
{
    public List<RepoOutcome> Outcomes { get; } = [];
    public List<string> Rescued { get; } = [];
    public bool AllSucceeded => Outcomes.All(o => o.Success);
    public IEnumerable<string> Switched => Outcomes.Where(o => o.Success).Select(o => o.Repo);
    public IEnumerable<RepoOutcome> Failed => Outcomes.Where(o => !o.Success);
}

/// <summary>
/// Demo mode: every switch cleans the working folder automatically and checks the target out as a throwaway
/// <c>demo/...</c> branch. Commits made on a demo branch are saved on <c>rescue/...</c> before it is replaced.
/// </summary>
public sealed class DemoService(RepoService repos)
{
    /// <summary>Switches one repo to <c>demo/&lt;snapshot&gt;</c>, discarding all uncommitted changes.</summary>
    public async Task<string?> SwitchRepoAsync(RepoState repo, string snapshot, SwitchReport report)
    {
        var previous = repo.CurrentBranch;
        var rescue = await RescueIfNeededAsync(repo.Path, previous);
        if (rescue != null)
            report.Rescued.Add($"{repo.Name}: {rescue}");

        var demo = BranchNames.DemoFor(snapshot);
        await repos.Git.RunCheckedAsync(repo.Path, "clean", "-fd");
        await repos.Git.RunCheckedAsync(repo.Path, "switch", "-C", demo, snapshot, "--discard-changes");

        if (BranchNames.IsDemo(previous) && previous != demo)
            await repos.DeleteBranchAsync(repo.Path, previous!);
        await DeleteStrayDemoBranchesAsync(repo.Path, keep: demo);
        return rescue;
    }

    /// <summary>Switches every repo that has <paramref name="snapshot"/>; the others stay where they are.</summary>
    public async Task<SwitchReport> SwitchAsync(IEnumerable<RepoState> states, string snapshot)
    {
        var report = new SwitchReport();
        foreach (var repo in states.Where(r => r.HasBranch(snapshot)))
        {
            try
            {
                await SwitchRepoAsync(repo, snapshot, report);
                report.Outcomes.Add(new RepoOutcome(repo.Name, true, BranchNames.DemoFor(snapshot)));
            }
            catch (Exception ex)
            {
                report.Outcomes.Add(new RepoOutcome(repo.Name, false, ex.Message));
            }
        }
        return report;
    }

    /// <summary>Cleans the current demo branch back to its snapshot (for a live demo that went wrong).</summary>
    public async Task<SwitchReport> ResetAsync(IEnumerable<RepoState> states)
    {
        var report = new SwitchReport();
        foreach (var repo in states.Where(r => r.IsOnDemoBranch))
        {
            try
            {
                var snapshot = BranchNames.SnapshotOfDemo(repo.CurrentBranch!);
                await SwitchRepoAsync(repo, snapshot, report);
                report.Outcomes.Add(new RepoOutcome(repo.Name, true, repo.CurrentBranch!));
            }
            catch (Exception ex)
            {
                report.Outcomes.Add(new RepoOutcome(repo.Name, false, ex.Message));
            }
        }
        return report;
    }

    /// <summary>
    /// Preparation to Demo: every repo that is on an exercise branch moves to the matching demo branch.
    /// The caller has already made sure all repos are clean.
    /// </summary>
    public async Task<SwitchReport> EnterAsync(IEnumerable<RepoState> states)
    {
        var report = new SwitchReport();
        foreach (var repo in states)
        {
            if (repo.CurrentBranch is not { } branch || !ExerciseBranch.TryParse(branch, out _))
            {
                report.Outcomes.Add(new RepoOutcome(repo.Name, true, $"stays on {repo.CurrentBranch ?? "(detached)"}"));
                continue;
            }
            try
            {
                if (repo.IsDirty)
                    throw new InvalidOperationException("working folder is not clean");
                await SwitchRepoAsync(repo, branch, report);
                report.Outcomes.Add(new RepoOutcome(repo.Name, true, BranchNames.DemoFor(branch)));
            }
            catch (Exception ex)
            {
                report.Outcomes.Add(new RepoOutcome(repo.Name, false, ex.Message));
            }
        }
        return report;
    }

    /// <summary>
    /// Demo to Preparation: discard demo changes, rescue stray commits, return every repo to a real branch
    /// and delete the demo branches.
    /// </summary>
    public async Task<SwitchReport> LeaveAsync(IEnumerable<RepoState> states, CourseState state)
    {
        var report = new SwitchReport();
        foreach (var repo in states)
        {
            if (!repo.IsOnDemoBranch)
            {
                await DeleteStrayDemoBranchesAsync(repo.Path, keep: null);
                report.Outcomes.Add(new RepoOutcome(repo.Name, true, $"stays on {repo.CurrentBranch ?? "(detached)"}"));
                continue;
            }
            try
            {
                var demo = repo.CurrentBranch!;
                var rescue = await RescueIfNeededAsync(repo.Path, demo);
                if (rescue != null)
                    report.Rescued.Add($"{repo.Name}: {rescue}");

                var target = ReturnBranch(repo, state);
                await repos.DiscardAllAsync(repo.Path);
                await repos.SwitchAsync(repo.Path, target);
                await DeleteStrayDemoBranchesAsync(repo.Path, keep: null);
                report.Outcomes.Add(new RepoOutcome(repo.Name, true, target));
            }
            catch (Exception ex)
            {
                report.Outcomes.Add(new RepoOutcome(repo.Name, false, ex.Message));
            }
        }
        return report;
    }

    /// <summary>Where a repo returns to when leaving Demo mode. Never a demo branch.</summary>
    public static string ReturnBranch(RepoState repo, CourseState state)
    {
        if (state.PrepPosition is { Repo: { } r, Branch: { } b } && r == repo.Name && repo.HasBranch(b) && !BranchNames.IsReserved(b))
            return b;
        if (state.PrepBranches.TryGetValue(repo.Name, out var last) && repo.HasBranch(last) && !BranchNames.IsReserved(last))
            return last;
        if (repo.CurrentBranch is { } current && BranchNames.IsDemo(current)
            && ExerciseBranch.TryParse(BranchNames.SnapshotOfDemo(current), out var snapshot))
        {
            var solution = ExerciseBranch.NameFor(snapshot.Week, "solution");
            if (repo.HasBranch(solution))
                return solution;
            if (repo.HasBranch(snapshot.Name))
                return snapshot.Name;
        }
        return repo.ExerciseBranches.LastOrDefault()?.Name
            ?? (repo.HasBranch("main") ? "main" : repo.Branches.Keys.First(b => !BranchNames.IsReserved(b)));
    }

    /// <summary>
    /// If <paramref name="demoBranch"/> has commits beyond its snapshot, keep them on a new rescue branch.
    /// Returns the rescue branch name, or null when nothing needed saving.
    /// </summary>
    public async Task<string?> RescueIfNeededAsync(string path, string? demoBranch)
    {
        if (!BranchNames.IsDemo(demoBranch))
            return null;
        var snapshot = BranchNames.SnapshotOfDemo(demoBranch!);
        var range = await repos.ResolveAsync(path, snapshot) != null ? $"{snapshot}..{demoBranch}" : demoBranch!;
        if (await repos.CountCommitsAsync(path, range) == 0)
            return null;
        var rescue = BranchNames.NewRescueName();
        while (await repos.ResolveAsync(path, rescue) != null)
            rescue += "x";
        await repos.CreateBranchAsync(path, rescue, demoBranch!);
        repos.Git.Info($"Commits on {demoBranch} saved on {rescue}.", Path.GetFileName(path));
        return rescue;
    }

    private async Task DeleteStrayDemoBranchesAsync(string path, string? keep)
    {
        var state = await repos.ReadStateAsync(path);
        foreach (var branch in state.Branches.Keys.Where(b => BranchNames.IsDemo(b) && b != keep && b != state.CurrentBranch))
        {
            await RescueIfNeededAsync(path, branch);
            await repos.DeleteBranchAsync(path, branch);
        }
    }
}
