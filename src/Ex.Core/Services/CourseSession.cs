using Ex.Core.Course;
using Ex.Core.Git;
using Ex.Core.Model;

namespace Ex.Core.Services;

public enum CleanChoice { Commit, Stash, Discard }

/// <summary>
/// An opened course folder: the YAML, the scanned repos and the combined week overview, plus the actions that
/// touch more than one of them (mode switches, recording the Preparation position). UI-independent.
/// </summary>
public sealed class CourseSession
{
    public CourseSession(CourseFolder course, GitRunner git)
    {
        Course = course;
        Git = git;
        Repos = new RepoService(git);
        Demo = new DemoService(Repos);
        Propagate = new PropagateService(Repos);
        Check = new CheckService(Repos);
        Overview = new OverviewBuilder(Repos);
    }

    public CourseFolder Course { get; }
    public GitRunner Git { get; }
    public RepoService Repos { get; }
    public DemoService Demo { get; }
    public PropagateService Propagate { get; }
    public CheckService Check { get; }
    public OverviewBuilder Overview { get; }

    public CourseManifest Manifest => Course.Manifest;
    public ClientMode Mode => Manifest.State.Mode;

    public IReadOnlyList<RepoState> States { get; private set; } = [];
    public IReadOnlyList<WeekOverview> Weeks { get; private set; } = [];

    /// <summary>A propagation that stopped at a merge conflict and waits for Continue or Abort.</summary>
    public PropagateProgress? PendingPropagation { get; set; }

    public async Task RefreshAsync()
    {
        var states = new List<RepoState>();
        foreach (var path in Course.FindRepoPaths())
            states.Add(await Repos.ReadStateAsync(path));
        States = states;
        Weeks = await Overview.BuildAsync(Manifest, States);
    }

    public RepoState? FindRepo(string name) =>
        States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<RepoState> DirtyRepos => States.Where(s => s.IsDirty);

    public void Save() => Course.Save();

    /// <summary>
    /// Compares the saved mode with the repos (after a crash or an interrupted switch).
    /// Returns a description of the mismatch, or null when everything agrees.
    /// </summary>
    public string? DescribeModeMismatch()
    {
        var onDemo = States.Where(s => s.IsOnDemoBranch).Select(s => $"{s.Name} ({s.CurrentBranch})").ToList();
        if (Mode == ClientMode.Preparation && onDemo.Count > 0)
            return $"exercises.yaml says Preparation mode, but these repos are on a demo branch: {string.Join(", ", onDemo)}.";
        if (Mode == ClientMode.Demo && onDemo.Count == 0 && States.Any(s => s.ExerciseBranches.Any()))
            return "exercises.yaml says Demo mode, but no repo is on a demo branch.";
        return null;
    }

    // ---- Preparation ----------------------------------------------------------------------

    public async Task CleanAsync(IEnumerable<RepoState> repos, CleanChoice choice, string? commitMessage)
    {
        foreach (var repo in repos.Where(r => r.IsDirty))
        {
            switch (choice)
            {
                case CleanChoice.Commit:
                    await Repos.CommitAllAsync(repo.Path, commitMessage ?? "Work in progress");
                    break;
                case CleanChoice.Stash:
                    var name = await Repos.StashAllAsync(repo.Path);
                    Git.Info($"Stashed as '{name}'.", repo.Name);
                    break;
                case CleanChoice.Discard:
                    await Repos.DiscardAllAsync(repo.Path);
                    break;
            }
        }
    }

    /// <summary>
    /// Switches every repo that has <paramref name="branch"/> to it (Preparation mode, real branches).
    /// The repos must be clean; the caller handles the dialog.
    /// </summary>
    public async Task<SwitchReport> SwitchPrepAsync(string branch, string? primaryRepo)
    {
        await RefreshAsync();
        var report = new SwitchReport();
        foreach (var repo in States.Where(r => r.HasBranch(branch)))
        {
            if (repo.CurrentBranch == branch)
            {
                report.Outcomes.Add(new RepoOutcome(repo.Name, true, "already there"));
                continue;
            }
            try
            {
                if (repo.IsDirty)
                    throw new InvalidOperationException("working folder is not clean");
                await Repos.SwitchAsync(repo.Path, branch);
                report.Outcomes.Add(new RepoOutcome(repo.Name, true, branch));
            }
            catch (Exception ex)
            {
                report.Outcomes.Add(new RepoOutcome(repo.Name, false, ex.Message));
            }
        }

        var primary = report.Outcomes.FirstOrDefault(o => o.Success && o.Repo == primaryRepo)?.Repo
                      ?? report.Switched.FirstOrDefault();
        foreach (var repo in report.Switched)
            RecordPrepBranch(repo, branch, setPosition: repo == primary);
        Save();
        return report;
    }

    public void RecordPrepBranch(string repo, string branch, bool setPosition = true)
    {
        if (BranchNames.IsReserved(branch))
            return;
        Manifest.State.PrepBranches[repo] = branch;
        if (setPosition)
            Manifest.State.PrepPosition = new RepoBranch { Repo = repo, Branch = branch };
    }

    public async Task NewWeekAsync(RepoState repo, int week, string? source, string? title)
    {
        await Repos.CreateWeekAsync(repo.Path, week, source);
        var entry = Manifest.GetOrAddWeek(ExerciseBranch.WeekIdFor(week));
        if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(entry.Title))
            entry.Title = title;
        if (!entry.Repos.Contains(repo.Name, StringComparer.OrdinalIgnoreCase))
            entry.Repos.Add(repo.Name);
        RecordPrepBranch(repo.Name, ExerciseBranch.NameFor(week, "solution"));
        Save();
    }

    public async Task AddSnapshotAsync(RepoState repo, int week, int step, string commit, string? title)
    {
        var stepId = $"step-{step}";
        await Repos.CreateBranchAsync(repo.Path, ExerciseBranch.NameFor(week, stepId), commit);
        var entry = Manifest.GetOrAddWeek(ExerciseBranch.WeekIdFor(week));
        var stepEntry = entry.GetOrAddStep(stepId);
        if (!string.IsNullOrWhiteSpace(title))
            stepEntry.Title = title;
        entry.Steps.Sort((a, b) => StepNumber(a.Id).CompareTo(StepNumber(b.Id)));
        if (!entry.Repos.Contains(repo.Name, StringComparer.OrdinalIgnoreCase))
            entry.Repos.Add(repo.Name);
        Save();
    }

    /// <summary>
    /// Saves the work of a week: commits the changes (when there are any) in every given repo and, when
    /// <paramref name="markAsStep"/> is set, marks the result as the next demo step (<c>weekNN/step-N</c>).
    /// Returns the step branch, or null.
    /// </summary>
    public async Task<string?> SaveWorkAsync(IReadOnlyList<RepoState> repos, int week, string? message, bool markAsStep, string? stepTitle)
    {
        foreach (var repo in repos.Where(r => r.IsDirty))
            await Repos.CommitAllAsync(repo.Path, string.IsNullOrWhiteSpace(message) ? "Work in progress" : message);
        if (!markAsStep)
            return null;

        var step = repos.Select(r => NextStepNumber(r, week)).DefaultIfEmpty(1).Max();
        foreach (var repo in repos)
            await AddSnapshotAsync(repo, week, step, "HEAD", stepTitle);
        return ExerciseBranch.NameFor(week, $"step-{step}");
    }

    public static int NextStepNumber(RepoState repo, int week) =>
        repo.ExerciseBranches.Where(b => b.Week == week && b.Kind == BranchKind.Step).Select(b => b.Step).DefaultIfEmpty(0).Max() + 1;

    public static int NextWeekNumber(IEnumerable<RepoState> repos) =>
        repos.SelectMany(r => r.ExerciseBranches).Select(b => b.Week).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Uncommitted files bigger than the large-file limit (no LFS: this is the safety net before a commit).</summary>
    public IReadOnlyList<TreeFile> LargeUncommittedFiles(RepoState repo)
    {
        var limit = Manifest.Settings.LargeFileMb * 1024L * 1024L;
        var result = new List<TreeFile>();
        foreach (var entry in repo.Status)
        {
            var full = Path.Combine(repo.Path, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                var size = new FileInfo(full).Length;
                if (size > limit)
                    result.Add(new TreeFile(entry.Path, size));
            }
        }
        return result;
    }

    // ---- Modes ----------------------------------------------------------------------------

    /// <summary>
    /// Preparation to Demo. All repos must already be clean. The mode is written to the YAML only when every repo
    /// switched successfully.
    /// </summary>
    public async Task<SwitchReport> EnterDemoAsync()
    {
        await RefreshAsync();
        if (DirtyRepos.Any())
            throw new InvalidOperationException("All repos must be clean before entering Demo mode.");
        var report = await Demo.EnterAsync(States);
        if (report.AllSucceeded)
        {
            Manifest.State.Mode = ClientMode.Demo;
            Save();
        }
        return report;
    }

    /// <summary>Demo to Preparation. Instant: demo changes are discarded, stray commits rescued.</summary>
    public async Task<SwitchReport> LeaveDemoAsync()
    {
        await RefreshAsync();
        var report = await Demo.LeaveAsync(States, Manifest.State);
        if (report.AllSucceeded)
        {
            Manifest.State.Mode = ClientMode.Preparation;
            Save();
        }
        return report;
    }

    /// <summary>Puts repos that already switched to Demo back on their Preparation branch (after a failed Enter).</summary>
    public async Task<SwitchReport> RollBackEnterDemoAsync()
    {
        await RefreshAsync();
        return await Demo.LeaveAsync(States.Where(s => s.IsOnDemoBranch), Manifest.State);
    }

    public async Task<SwitchReport> DemoSwitchAsync(string snapshot)
    {
        await RefreshAsync();
        return await Demo.SwitchAsync(States, snapshot);
    }

    public async Task<SwitchReport> DemoResetAsync()
    {
        await RefreshAsync();
        return await Demo.ResetAsync(States);
    }

    /// <summary>Writes the mode without switching any repo (used to fix a mismatch found at startup).</summary>
    public void ForceMode(ClientMode mode)
    {
        Manifest.State.Mode = mode;
        Save();
    }

    private static int StepNumber(string stepId) =>
        stepId.StartsWith("step-") && int.TryParse(stepId[5..], out var n) ? n : int.MaxValue;
}
