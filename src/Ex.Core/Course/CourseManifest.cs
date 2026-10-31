namespace Ex.Core.Course;

public enum ClientMode { Preparation, Demo }

/// <summary>
/// The contents of <c>exercises.yaml</c>. Git is the source of truth for structure (which branches exist);
/// this file only holds what git cannot: settings, titles, notes and the client state.
/// </summary>
public sealed class CourseManifest
{
    public CourseInfo Course { get; set; } = new();
    public CourseSettings Settings { get; set; } = new();
    public CourseState State { get; set; } = new();
    public List<WeekEntry> Weeks { get; set; } = [];

    public WeekEntry? FindWeek(string weekId) =>
        Weeks.FirstOrDefault(w => string.Equals(w.Id, weekId, StringComparison.OrdinalIgnoreCase));

    public WeekEntry GetOrAddWeek(string weekId)
    {
        var week = FindWeek(weekId);
        if (week != null)
            return week;
        week = new WeekEntry { Id = weekId };
        Weeks.Add(week);
        Weeks.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return week;
    }
}

public sealed class CourseInfo
{
    public string? Name { get; set; }
    public string? Year { get; set; }
}

public sealed class CourseSettings
{
    /// <summary>Folder that holds the exercise repos, relative to the folder of <c>exercises.yaml</c>.</summary>
    public string ProjectsDir { get; set; } = "Projects";

    /// <summary>Remembered from the last export; the folder is still picked each time.</summary>
    public string? LastExportDir { get; set; }

    public int LargeFileMb { get; set; } = 5;

    public string UnityBaseline { get; set; } = "6.6";
}

public sealed class CourseState
{
    /// <summary>Only written after a mode switch has fully succeeded.</summary>
    public ClientMode Mode { get; set; } = ClientMode.Preparation;

    /// <summary>Last position in Preparation mode; where to return to when leaving Demo mode.</summary>
    public RepoBranch? PrepPosition { get; set; }

    /// <summary>Last real branch used in Preparation mode, per repo.</summary>
    public Dictionary<string, string> PrepBranches { get; set; } = [];
}

public sealed class RepoBranch
{
    public string? Repo { get; set; }
    public string? Branch { get; set; }
}

public sealed class WeekEntry
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public List<string> Repos { get; set; } = [];
    public string? Notes { get; set; }

    /// <summary>Syllabus chapter this week builds on. Informational only.</summary>
    public string? Theory { get; set; }

    public List<StepEntry> Steps { get; set; } = [];

    public StepEntry? FindStep(string stepId) =>
        Steps.FirstOrDefault(s => string.Equals(s.Id, stepId, StringComparison.OrdinalIgnoreCase));

    public StepEntry GetOrAddStep(string stepId)
    {
        var step = FindStep(stepId);
        if (step != null)
            return step;
        step = new StepEntry { Id = stepId };
        Steps.Add(step);
        return step;
    }
}

public sealed class StepEntry
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
}
