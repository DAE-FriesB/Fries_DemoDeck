using Ex.Core.Course;
using Ex.Core.Model;
using Ex.Core.Services;

namespace Ex.Core.Tests;

public class ExerciseBranchTests
{
    [Theory]
    [InlineData("week03/start", 3, BranchKind.Start, 0)]
    [InlineData("week03/step-2", 3, BranchKind.Step, 2)]
    [InlineData("week12/solution", 12, BranchKind.Solution, 0)]
    public void Parses_valid_names(string name, int week, BranchKind kind, int step)
    {
        Assert.True(ExerciseBranch.TryParse(name, out var b));
        Assert.Equal(week, b.Week);
        Assert.Equal(kind, b.Kind);
        Assert.Equal(step, b.Step);
    }

    [Theory]
    [InlineData("week03")]
    [InlineData("week3/start")]
    [InlineData("demo/week03/start")]
    [InlineData("week03/step2")]
    [InlineData("main")]
    public void Rejects_other_names(string name) => Assert.False(ExerciseBranch.TryParse(name, out _));

    [Fact]
    public void Orders_start_steps_solution_then_next_week()
    {
        var names = new[] { "week02/start", "week01/solution", "week01/step-10", "week01/start", "week01/step-2" };
        var ordered = names.Select(n => { ExerciseBranch.TryParse(n, out var b); return b; }).Order().Select(b => b.Name);
        Assert.Equal(["week01/start", "week01/step-2", "week01/step-10", "week01/solution", "week02/start"], ordered);
    }

    [Fact]
    public void Demo_names_round_trip()
    {
        var demo = BranchNames.DemoFor("week03/step-2");
        Assert.Equal("demo/week03/step-2", demo);
        Assert.True(BranchNames.IsReserved(demo));
        Assert.Equal("week03/step-2", BranchNames.SnapshotOfDemo(demo));
    }
}

public class ManifestTests
{
    [Fact]
    public void Round_trips_with_snake_case_keys()
    {
        var manifest = new CourseManifest
        {
            Course = new CourseInfo { Name = "My course", Year = "2026-2027" },
            Settings = new CourseSettings { LastExportDir = @"D:\Lms" },
        };
        manifest.State.Mode = ClientMode.Demo;
        manifest.State.PrepPosition = new RepoBranch { Repo = "unity-project", Branch = "week03/solution" };
        var week = manifest.GetOrAddWeek("week03");
        week.Title = "Netcode";
        week.Repos.Add("unity-project");
        week.GetOrAddStep("step-1").Title = "Connecting";

        var yaml = ManifestStore.Serialize(manifest);
        Assert.Contains("projects_dir: Projects", yaml);
        Assert.Contains("large_file_mb: 5", yaml);
        Assert.Contains("mode: demo", yaml);
        Assert.Contains("prep_position:", yaml);

        var back = ManifestStore.Parse(yaml);
        Assert.Equal(ClientMode.Demo, back.State.Mode);
        Assert.Equal("week03/solution", back.State.PrepPosition?.Branch);
        Assert.Equal("Connecting", back.FindWeek("week03")?.FindStep("step-1")?.Title);
        Assert.Equal(@"D:\Lms", back.Settings.LastExportDir);
    }

    [Fact]
    public void Tolerates_unknown_keys_and_missing_sections()
    {
        var back = ManifestStore.Parse("course:\n  name: X\nsomething_new: 1\nweeks:\n  - id: week01\n");
        Assert.Equal("X", back.Course.Name);
        Assert.Equal("Projects", back.Settings.ProjectsDir);
        Assert.Equal(ClientMode.Preparation, back.State.Mode);
        Assert.Empty(back.Weeks[0].Steps);
    }

    [Theory]
    [InlineData("6.6", "6000.6.")]
    [InlineData("6.10", "6000.10.")]
    [InlineData("2022.3", "2022.3")]
    public void Unity_baseline_maps_to_editor_version_prefix(string baseline, string prefix) =>
        Assert.Equal(prefix, CheckService.UnityVersionPrefix(baseline));

    [Fact]
    public void Reads_unity_editor_version() =>
        Assert.Equal("6000.6.0f1", CheckService.ParseUnityVersion("m_EditorVersion: 6000.6.0f1\nm_EditorVersionWithRevision: 6000.6.0f1 (abc)\n"));
}
