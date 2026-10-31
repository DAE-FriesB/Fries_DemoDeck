using Ex.Core.Course;
using Ex.Core.Model;
using Ex.Core.Services;

namespace Ex.Core.Tests;

/// <summary>End-to-end tests against real git repositories in a temp folder.</summary>
public class GitWorkflowTests
{
    /// <summary>week01: start, step-1, solution; week02 continues from week01/solution.</summary>
    private static async Task<TempCourse> TwoWeeksAsync()
    {
        var t = await TempCourse.CreateAsync();
        var repo = await t.StateAsync();
        await t.Session.NewWeekAsync(repo, 1, "main", "First week");
        await t.CommitAsync("Program.cs", "v1 // bug", "step 1");
        await t.Repos.CreateBranchAsync(t.RepoPath, "week01/step-1", "HEAD");
        await t.CommitAsync("Other.cs", "other", "step 2");
        repo = await t.StateAsync();
        await t.Session.NewWeekAsync(repo, 2, "week01/solution", null);
        await t.CommitAsync("Week2.cs", "w2", "week 2 work");
        return t;
    }

    [Fact]
    public async Task New_week_creates_start_and_solution_and_records_yaml()
    {
        using var t = await TwoWeeksAsync();
        var repo = await t.StateAsync();
        Assert.Equal("week02/solution", repo.CurrentBranch);
        Assert.True(repo.HasBranch("week01/start"));
        Assert.True(repo.HasBranch("week02/start"));
        Assert.Equal("First week", t.Session.Manifest.FindWeek("week01")?.Title);
        Assert.Contains("test-repo", t.Session.Manifest.FindWeek("week02")!.Repos);

        var week2 = t.Session.Weeks.Single(w => w.Id == "week02");
        Assert.True(week2.Continuation["test-repo"]);
        Assert.Equal(["start", "solution"], week2.Entries.Select(e => e.EntryId));
        Assert.Equal(["start", "step-1", "solution"], t.Session.Weeks.Single(w => w.Id == "week01").Entries.Select(e => e.EntryId));
    }

    [Fact]
    public async Task Save_work_commits_and_marks_the_next_step()
    {
        using var t = await TwoWeeksAsync();
        t.Write("Week2.cs", "w2 step");
        var repo = await t.StateAsync();
        var step = await t.Session.SaveWorkAsync([repo], 2, "add step", markAsStep: true, "Syncing movement");
        Assert.Equal("week02/step-1", step);

        repo = await t.StateAsync();
        Assert.False(repo.IsDirty);
        Assert.Equal(repo.Branches["week02/solution"], repo.Branches["week02/step-1"]);
        Assert.Equal("Syncing movement", t.Session.Manifest.FindWeek("week02")?.FindStep("step-1")?.Title);

        Assert.Null(await t.Session.SaveWorkAsync([repo], 2, null, markAsStep: false, null));
    }

    [Fact]
    public async Task Discards_only_the_selected_changes_and_shows_diffs()
    {
        using var t = await TwoWeeksAsync();
        t.Write("Week2.cs", "w2 changed");          // modified, discard
        t.Write("Program.cs", "kept change");       // modified, keep
        t.Write("New.cs", "line 1\nline 2\n");       // new, discard
        t.Write("NewKept.cs", "kept");              // new, keep
        File.Delete(Path.Combine(t.RepoPath, "Other.cs")); // deleted, discard

        var repo = await t.StateAsync();
        Assert.Equal(5, repo.Status.Count);
        var byPath = repo.Status.ToDictionary(e => e.Path);
        Assert.Equal(ChangeKind.Untracked, byPath["New.cs"].Kind);
        Assert.Equal(ChangeKind.Deleted, byPath["Other.cs"].Kind);
        Assert.Equal(ChangeKind.Modified, byPath["Week2.cs"].Kind);

        var diff = await t.Repos.DiffFileAsync(t.RepoPath, byPath["Week2.cs"]);
        Assert.Contains("-w2", diff);
        Assert.Contains("+w2 changed", diff);
        Assert.Contains("+line 2", await t.Repos.DiffFileAsync(t.RepoPath, byPath["New.cs"]));

        await t.Repos.DiscardFilesAsync(t.RepoPath, [byPath["Week2.cs"], byPath["New.cs"], byPath["Other.cs"]]);

        repo = await t.StateAsync();
        Assert.Equal(["NewKept.cs", "Program.cs"], repo.Status.Select(e => e.Path).Order());
        Assert.Equal("w2", t.Read("Week2.cs"));
        Assert.False(t.Exists("New.cs"));
        Assert.True(t.Exists("Other.cs"));
        Assert.Equal("kept change", t.Read("Program.cs"));
    }

    [Fact]
    public async Task Empty_week_keeps_ignore_rules_and_is_a_fresh_start()
    {
        using var t = await TwoWeeksAsync();
        var repo = await t.StateAsync();
        await t.Session.NewWeekAsync(repo, 3, null, null);
        Assert.True(t.Exists(".gitignore"));
        Assert.False(t.Exists("Program.cs"));
        await t.StateAsync();
        Assert.False(t.Session.Weeks.Single(w => w.Id == "week03").Continuation["test-repo"]);
    }

    [Fact]
    public async Task Propagate_merges_fix_forward_in_order()
    {
        using var t = await TwoWeeksAsync();
        var repo = await t.StateAsync();
        await t.Session.SwitchPrepAsync("week01/step-1", "test-repo");
        await t.CommitAsync("Program.cs", "v1 // fixed", "fix bug");

        repo = await t.StateAsync();
        var plan = await t.Session.Propagate.PlanAsync(repo, "week01/step-1");
        Assert.Equal(
            [new MergeStep("week01/step-1", "week01/solution"), new MergeStep("week01/solution", "week02/start"), new MergeStep("week02/start", "week02/solution")],
            plan.Steps);

        var progress = await t.Session.Propagate.RunAsync(new PropagateProgress { Plan = plan });
        Assert.Equal(PropagateStatus.Completed, progress.Status);
        foreach (var branch in new[] { "week01/solution", "week02/start", "week02/solution" })
            Assert.Equal("v1 // fixed", await t.FileAtAsync(branch, "Program.cs"));
        Assert.Equal("w2", await t.FileAtAsync("week02/solution", "Week2.cs"));
        Assert.Equal("week01/step-1", (await t.StateAsync()).CurrentBranch);

        // Upstream is untouched; a second run has nothing left to do.
        Assert.Null(await t.Repos.ReadFileAtAsync(t.RepoPath, "week01/start", "Program.cs"));
        Assert.Empty((await t.Session.Propagate.PlanAsync(await t.StateAsync(), "week01/step-1")).Steps);
    }

    [Fact]
    public async Task Detects_saves_on_an_older_step_that_later_steps_do_not_have()
    {
        using var t = await TwoWeeksAsync();
        await t.Session.SwitchPrepAsync("week01/step-1", "test-repo");
        await t.CommitAsync("Program.cs", "v1 // fixed", "late save on step 1");
        await t.StateAsync();

        Assert.Equal(1, Entry("week01", "step-1").Unsynced);
        Assert.Equal(0, Entry("week01", "solution").Unsynced);

        var plan = await t.Session.Propagate.PlanAsync(t.Session.States.Single(), "week01/step-1");
        await t.Session.Propagate.RunAsync(new PropagateProgress { Plan = plan });
        await t.StateAsync();
        Assert.Equal(0, Entry("week01", "step-1").Unsynced);

        EntryOverview Entry(string week, string entry) =>
            t.Session.Weeks.Single(w => w.Id == week).Entries.Single(e => e.EntryId == entry);
    }

    [Fact]
    public async Task A_save_on_a_solution_keeps_the_next_week_a_continuation()
    {
        using var t = await TwoWeeksAsync();
        await t.Session.SwitchPrepAsync("week01/solution", "test-repo");
        await t.CommitAsync("Other.cs", "other, polished", "late save on the solution");
        await t.StateAsync();

        Assert.True(t.Session.Weeks.Single(w => w.Id == "week02").Continuation["test-repo"]);
        Assert.Equal(1, t.Session.Weeks.Single(w => w.Id == "week01").Entries.Single(e => e.EntryId == "solution").Unsynced);
    }

    [Fact]
    public async Task Keeping_the_later_version_is_decided_once_and_carried_forward()
    {
        // Like a Unity scene: step 1 is saved late with its own version of a file that later steps changed too.
        using var t = await TempCourse.CreateAsync();
        await t.Session.NewWeekAsync(await t.StateAsync(), 1, "main", null);
        await t.CommitAsync("Scene.unity", "objects: []", "empty scene");
        await t.Repos.CreateBranchAsync(t.RepoPath, "week01/step-1", "HEAD");
        await t.CommitAsync("Scene.unity", "objects: [SessionUI from step 2]", "step 2 adds the UI");
        await t.Repos.CreateBranchAsync(t.RepoPath, "week01/step-2", "HEAD");
        await t.CommitAsync("Scene.unity", "objects: [SessionUI from step 2, NetworkManager]", "finish");
        await t.Session.NewWeekAsync(await t.StateAsync(), 2, "week01/solution", null);
        await t.CommitAsync("Week2.cs", "w2", "week 2");

        await t.Session.SwitchPrepAsync("week01/step-1", "test-repo");
        await t.CommitAsync("Scene.unity", "objects: [SessionUI made late on step 1]", "late UI on step 1");

        var plan = await t.Session.Propagate.PlanAsync(await t.StateAsync(), "week01/step-1");
        Assert.Equal(["week01/step-2", "week01/solution", "week02/start", "week02/solution"], plan.Steps.Select(s => s.Into));
        var predicted = await t.Session.Propagate.PredictConflictsAsync(plan);
        Assert.Equal(["Scene.unity"], predicted["week01/step-2"]);

        var progress = await t.Session.Propagate.RunAsync(new PropagateProgress { Plan = plan });
        Assert.Equal(PropagateStatus.Conflict, progress.Status);
        Assert.Equal("week01/step-2", progress.Current?.Into);

        await t.Session.Propagate.ResolveAsync(progress, new Dictionary<string, ConflictChoice> { ["Scene.unity"] = ConflictChoice.KeepLater });
        progress = await t.Session.Propagate.ContinueAsync(progress);

        // Settled once: the later hops merge without asking again.
        Assert.Equal(PropagateStatus.Completed, progress.Status);
        Assert.Equal("objects: [SessionUI from step 2]", await t.FileAtAsync("week01/step-2", "Scene.unity"));
        Assert.Equal("objects: [SessionUI from step 2, NetworkManager]", await t.FileAtAsync("week02/solution", "Scene.unity"));
        foreach (var branch in plan.Steps.Select(s => s.Into))
            Assert.True(await t.Repos.IsAncestorAsync(t.RepoPath, "week01/step-1", branch), branch);
        await t.StateAsync();
        Assert.All(t.Session.Weeks.SelectMany(w => w.Entries), e => Assert.Equal(0, e.Unsynced));
    }

    [Fact]
    public async Task Propagate_skips_fresh_start_weeks()
    {
        using var t = await TwoWeeksAsync();
        var repo = await t.StateAsync();
        await t.Session.NewWeekAsync(repo, 3, null, null);
        await t.Session.SwitchPrepAsync("week01/solution", "test-repo");
        await t.CommitAsync("Other.cs", "other fixed", "fix");
        var plan = await t.Session.Propagate.PlanAsync(await t.StateAsync(), "week01/solution");
        Assert.DoesNotContain(plan.Steps, s => s.Into.StartsWith("week03"));
        Assert.Contains(plan.Steps, s => s.Into == "week02/solution");
    }

    [Fact]
    public async Task Propagate_stops_at_conflict_and_continues_after_resolution()
    {
        using var t = await TwoWeeksAsync();
        await t.CommitAsync("Program.cs", "v2 week2 change", "week 2 changes the same line");
        await t.Session.RefreshAsync();
        await t.Session.SwitchPrepAsync("week01/step-1", "test-repo");
        await t.CommitAsync("Program.cs", "v1 // fixed", "fix bug");

        var plan = await t.Session.Propagate.PlanAsync(await t.StateAsync(), "week01/step-1");
        var progress = await t.Session.Propagate.RunAsync(new PropagateProgress { Plan = plan });
        Assert.Equal(PropagateStatus.Conflict, progress.Status);
        Assert.Equal("week02/solution", progress.Current?.Into);
        Assert.Equal(["Program.cs"], progress.ConflictFiles);

        t.Write("Program.cs", "v2 resolved");
        progress = await t.Session.Propagate.ContinueAsync(progress);
        Assert.Equal(PropagateStatus.Completed, progress.Status);
        Assert.Equal("v2 resolved", await t.FileAtAsync("week02/solution", "Program.cs"));
    }

    [Fact]
    public async Task Demo_mode_discards_changes_and_rescues_commits()
    {
        using var t = await TwoWeeksAsync();
        await t.Session.SwitchPrepAsync("week02/solution", "test-repo");

        var report = await t.Session.EnterDemoAsync();
        Assert.True(report.AllSucceeded);
        Assert.Equal(ClientMode.Demo, ManifestStore.Load(t.Session.Course.ManifestPath).State.Mode);
        Assert.Equal("demo/week02/solution", (await t.StateAsync()).CurrentBranch);

        // A live demo: change a tracked file, add a new file, and accidentally commit.
        t.Write("Program.cs", "changed in demo");
        t.Write("NewScript.cs", "new");
        await t.CommitAsync("Accident.cs", "oops", "accidental commit");
        t.Write("Program.cs", "changed again");

        await t.StateAsync();
        report = await t.Session.DemoSwitchAsync("week01/step-1");
        Assert.True(report.AllSucceeded);
        Assert.Single(report.Rescued);
        var repo = await t.StateAsync();
        Assert.Equal("demo/week01/step-1", repo.CurrentBranch);
        Assert.False(repo.IsDirty);
        Assert.False(t.Exists("NewScript.cs"));
        Assert.Equal("v1 // bug", t.Read("Program.cs"));
        Assert.DoesNotContain(repo.Branches.Keys, b => b == "demo/week02/solution");
        var rescue = Assert.Single(repo.Branches.Keys, BranchNames.IsRescue);
        Assert.Equal("oops", await t.FileAtAsync(rescue, "Accident.cs"));
        Assert.Equal("w2", await t.FileAtAsync("week02/solution", "Week2.cs"));
        Assert.Null(await t.Repos.ReadFileAtAsync(t.RepoPath, "week02/solution", "Accident.cs"));

        // Reset current step.
        t.Write("Temp.cs", "x");
        await t.StateAsync();
        await t.Session.DemoResetAsync();
        Assert.False((await t.StateAsync()).IsDirty);

        // Leaving returns to the Preparation position and removes the demo branch.
        t.Write("Program.cs", "dirty at the end of class");
        await t.StateAsync();
        report = await t.Session.LeaveDemoAsync();
        Assert.True(report.AllSucceeded);
        repo = await t.StateAsync();
        Assert.Equal("week02/solution", repo.CurrentBranch);
        Assert.False(repo.IsDirty);
        Assert.DoesNotContain(repo.Branches.Keys, BranchNames.IsDemo);
        Assert.Equal(ClientMode.Preparation, ManifestStore.Load(t.Session.Course.ManifestPath).State.Mode);
    }

    [Fact]
    public async Task Entering_demo_requires_a_clean_state()
    {
        using var t = await TwoWeeksAsync();
        t.Write("Program.cs", "uncommitted");
        await t.StateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Session.EnterDemoAsync());
        Assert.Equal(ClientMode.Preparation, t.Session.Mode);
    }

    [Fact]
    public async Task Mode_mismatch_is_detected()
    {
        using var t = await TwoWeeksAsync();
        await t.Repos.Git.RunCheckedAsync(t.RepoPath, "switch", "-c", "demo/week02/solution", "week02/solution");
        await t.StateAsync();
        Assert.NotNull(t.Session.DescribeModeMismatch());
        await t.Session.LeaveDemoAsync();
        await t.StateAsync();
        Assert.Null(t.Session.DescribeModeMismatch());
    }

    [Fact]
    public async Task Clean_with_stash_keeps_changes_restorable()
    {
        using var t = await TwoWeeksAsync();
        t.Write("Program.cs", "work in progress");
        t.Write("Untracked.cs", "new");
        var repo = await t.StateAsync();
        await t.Session.CleanAsync([repo], CleanChoice.Stash, null);
        Assert.False((await t.StateAsync()).IsDirty);
        var stash = Assert.Single(await t.Repos.ListStashesAsync(t.RepoPath));
        Assert.Contains(RepoService.StashPrefix, stash.Message);
        await t.Repos.ApplyStashAsync(t.RepoPath, stash.Ref);
        Assert.Equal("work in progress", t.Read("Program.cs"));
        Assert.True(t.Exists("Untracked.cs"));
    }

    [Fact]
    public async Task Export_and_check()
    {
        using var t = await TwoWeeksAsync();
        var zip = Path.Combine(t.Folder, "week02-start.zip");
        await t.Repos.ExportZipAsync(t.RepoPath, "week02/start", zip);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            Assert.Contains(archive.Entries, e => e.FullName == "Program.cs");
            Assert.DoesNotContain(archive.Entries, e => e.FullName.StartsWith(".git/"));
            Assert.DoesNotContain(archive.Entries, e => e.FullName == "Week2.cs");
        }

        await t.Repos.CreateBranchAsync(t.RepoPath, "week09", "main");
        t.Session.Manifest.GetOrAddWeek("week07");
        var issues = await t.Session.Check.RunAsync(t.Session.Manifest, [await t.StateAsync()]);
        Assert.Contains(issues, i => i.Message.Contains("bare branch 'week09'"));
        Assert.Contains(issues, i => i.Message.Contains("week07 has no branch"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("does not contain"));
    }
}
