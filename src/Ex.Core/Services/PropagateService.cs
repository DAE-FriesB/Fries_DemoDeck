using Ex.Core.Model;

namespace Ex.Core.Services;

/// <summary>One merge of the plan: <see cref="From"/> is merged into <see cref="Into"/>.</summary>
public sealed record MergeStep(string From, string Into);

/// <summary>How to settle one conflicting file.</summary>
public enum ConflictChoice
{
    /// <summary>Keep the version of the branch being updated (the later step): the change is marked as synced only.</summary>
    KeepLater,
    /// <summary>Take the version of the branch the fix comes from.</summary>
    TakeEarlier,
    /// <summary>The user edited the file by hand; it must not contain conflict markers any more.</summary>
    ResolvedByHand,
}

public sealed class PropagatePlan
{
    public required string RepoPath { get; init; }
    public required string Source { get; init; }
    public List<MergeStep> Steps { get; } = [];
    public string Repo => Path.GetFileName(RepoPath);
}

public enum PropagateStatus { Completed, Conflict, Failed }

public sealed class PropagateProgress
{
    public required PropagatePlan Plan { get; init; }
    public int NextStep { get; set; }
    public PropagateStatus Status { get; set; }
    public IReadOnlyList<string> ConflictFiles { get; set; } = [];
    public string? Error { get; set; }
    public MergeStep? Current => NextStep < Plan.Steps.Count ? Plan.Steps[NextStep] : null;
}

/// <summary>
/// Merge forward (D12): after a fix is committed on the first branch that contains the mistake, merge it into
/// every exercise branch that descends from it, in dependency order. History is never rewritten.
/// </summary>
public sealed class PropagateService(RepoService repos)
{
    /// <summary>
    /// Works out which branches descend from <paramref name="source"/> and in which order to merge.
    /// A later branch (by week and step order) is downstream when its merge base with the source lies at or after
    /// the nearest earlier exercise branch that the source itself contains.
    /// </summary>
    public async Task<PropagatePlan> PlanAsync(RepoState repo, string source)
    {
        if (!ExerciseBranch.TryParse(source, out var sourceBranch))
            throw new InvalidOperationException($"{source} is not an exercise branch (weekNN/start, step-N or solution).");

        var plan = new PropagatePlan { RepoPath = repo.Path, Source = source };
        var all = repo.ExerciseBranches.ToList();

        // Nearest earlier exercise branch contained in the source: the fork point of this part of the line.
        string? floor = null;
        foreach (var earlier in all.Where(b => b.CompareTo(sourceBranch) < 0).Reverse())
        {
            if (await repos.IsAncestorAsync(repo.Path, earlier.Name, source))
            {
                floor = earlier.Name;
                break;
            }
        }
        var floorCommit = floor != null ? repo.Branches[floor] : null;

        var downstream = new List<ExerciseBranch>();
        foreach (var candidate in all.Where(b => b.CompareTo(sourceBranch) > 0))
        {
            if (await repos.IsAncestorAsync(repo.Path, source, candidate.Name))
                continue; // already contains the fix
            var mergeBase = await repos.MergeBaseAsync(repo.Path, source, candidate.Name);
            if (mergeBase == null)
                continue; // unrelated history (fresh start)
            if (floorCommit != null && !await repos.IsAncestorAsync(repo.Path, floorCommit, mergeBase))
                continue; // branched off before this part of the line
            downstream.Add(candidate);
        }

        // Each hop merges from the latest earlier branch of the plan that it already descends from,
        // so a conflict resolved once is carried along instead of coming back at every branch.
        var done = new List<string> { source };
        foreach (var into in downstream)
        {
            var from = source;
            foreach (var previous in done.Skip(1).Reverse())
            {
                if (await repos.IsAncestorAsync(repo.Path, repo.Branches[previous], into.Name))
                {
                    from = previous;
                    break;
                }
            }
            plan.Steps.Add(new MergeStep(from, into.Name));
            done.Add(into.Name);
        }
        return plan;
    }

    /// <summary>
    /// Conflicts each hop would have if it were merged straight from the source, computed in memory. Later hops are
    /// an estimate: a conflict settled at an earlier hop is usually carried along and does not come back.
    /// </summary>
    public async Task<Dictionary<string, IReadOnlyList<string>>> PredictConflictsAsync(PropagatePlan plan)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var step in plan.Steps)
            result[step.Into] = await repos.PredictConflictsAsync(plan.RepoPath, step.Into, plan.Source);
        return result;
    }

    /// <summary>Settles conflicting files of the merge in progress, so <see cref="ContinueAsync"/> can commit it.</summary>
    public async Task ResolveAsync(PropagateProgress progress, IReadOnlyDictionary<string, ConflictChoice> choices)
    {
        var path = progress.Plan.RepoPath;
        foreach (var (file, choice) in choices)
        {
            if (choice == ConflictChoice.ResolvedByHand)
                continue;
            var side = choice == ConflictChoice.KeepLater ? "--ours" : "--theirs";
            var checkout = await repos.Git.RunAsync(path, "checkout", side, "--", file);
            if (!checkout.Success)
            {
                // That side deleted the file: deleting it is the resolution.
                await repos.Git.RunCheckedAsync(path, "rm", "-q", "--", file);
                continue;
            }
            await repos.Git.RunCheckedAsync(path, "add", "--", file);
        }
    }

    /// <summary>Runs the plan from <see cref="PropagateProgress.NextStep"/>, stopping at the first conflict.</summary>
    public async Task<PropagateProgress> RunAsync(PropagateProgress progress)
    {
        var path = progress.Plan.RepoPath;
        while (progress.Current is { } step)
        {
            try
            {
                await repos.SwitchAsync(path, step.Into);
                var result = await repos.Git.RunAsync(path, "merge", "--no-edit", "-m",
                    $"Propagate {progress.Plan.Source}: merge {step.From} into {step.Into}", step.From);
                if (!result.Success)
                {
                    var conflicts = await repos.UnmergedFilesAsync(path);
                    progress.ConflictFiles = conflicts;
                    progress.Status = conflicts.Count > 0 ? PropagateStatus.Conflict : PropagateStatus.Failed;
                    progress.Error = conflicts.Count > 0 ? null : (result.StdErr + result.StdOut).Trim();
                    return progress;
                }
            }
            catch (Exception ex)
            {
                progress.Status = PropagateStatus.Failed;
                progress.Error = ex.Message;
                return progress;
            }
            progress.NextStep++;
        }

        progress.Status = PropagateStatus.Completed;
        await repos.SwitchAsync(path, progress.Plan.Source);
        return progress;
    }

    /// <summary>
    /// After the conflicts were resolved by hand: commit the merge and continue with the rest of the plan.
    /// A file counts as resolved once it has no conflict markers left (no <c>git add</c> needed).
    /// </summary>
    public async Task<PropagateProgress> ContinueAsync(PropagateProgress progress)
    {
        var path = progress.Plan.RepoPath;
        var unresolved = (await repos.UnmergedFilesAsync(path)).Where(f => HasConflictMarkers(path, f)).ToList();
        if (unresolved.Count > 0)
        {
            progress.ConflictFiles = unresolved;
            progress.Status = PropagateStatus.Conflict;
            return progress;
        }
        await repos.Git.RunCheckedAsync(path, "add", "-A");
        await repos.Git.RunCheckedAsync(path, "commit", "--no-edit");
        progress.NextStep++;
        return await RunAsync(progress);
    }

    private static bool HasConflictMarkers(string repoPath, string file)
    {
        var full = Path.Combine(repoPath, file.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
            return false; // resolved by deleting the file
        foreach (var line in File.ReadLines(full))
        {
            if (line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line.StartsWith(">>>>>>> ", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Aborts the conflicting merge. Branches merged before it stay merged.</summary>
    public async Task AbortAsync(PropagateProgress progress)
    {
        var path = progress.Plan.RepoPath;
        await repos.Git.RunAsync(path, "merge", "--abort");
        await repos.SwitchAsync(path, progress.Plan.Source);
    }
}
