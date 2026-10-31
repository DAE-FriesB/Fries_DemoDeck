using System.Text.RegularExpressions;

namespace Ex.Core.Model;

public enum BranchKind { Start, Step, Solution }

/// <summary>A branch that follows the naming rule <c>weekNN/start</c>, <c>weekNN/step-N</c> or <c>weekNN/solution</c>.</summary>
public sealed partial record ExerciseBranch(string Name, int Week, BranchKind Kind, int Step) : IComparable<ExerciseBranch>
{
    public string WeekId => WeekIdFor(Week);

    /// <summary>The part after the slash: <c>start</c>, <c>step-2</c> or <c>solution</c>.</summary>
    public string EntryId => Kind switch
    {
        BranchKind.Start => "start",
        BranchKind.Solution => "solution",
        _ => $"step-{Step}",
    };

    /// <summary>Position inside the week: start first, then the steps, solution last.</summary>
    public int Order => Kind switch
    {
        BranchKind.Start => 0,
        BranchKind.Solution => int.MaxValue,
        _ => Step,
    };

    public int CompareTo(ExerciseBranch? other)
    {
        if (other is null) return 1;
        var byWeek = Week.CompareTo(other.Week);
        return byWeek != 0 ? byWeek : Order.CompareTo(other.Order);
    }

    public static string WeekIdFor(int week) => $"week{week:00}";

    public static string NameFor(int week, string entryId) => $"{WeekIdFor(week)}/{entryId}";

    public static bool TryParse(string name, out ExerciseBranch branch)
    {
        branch = null!;
        var m = BranchRegex().Match(name);
        if (!m.Success)
            return false;
        var week = int.Parse(m.Groups[1].Value);
        var entry = m.Groups[2].Value;
        branch = entry switch
        {
            "start" => new ExerciseBranch(name, week, BranchKind.Start, 0),
            "solution" => new ExerciseBranch(name, week, BranchKind.Solution, 0),
            _ => new ExerciseBranch(name, week, BranchKind.Step, int.Parse(m.Groups[3].Value)),
        };
        return true;
    }

    public static bool TryParseWeekId(string weekId, out int week)
    {
        week = 0;
        var m = WeekIdRegex().Match(weekId);
        return m.Success && int.TryParse(m.Groups[1].Value, out week);
    }

    [GeneratedRegex(@"^week(\d{2,})/(start|solution|step-(\d+))$")]
    private static partial Regex BranchRegex();

    [GeneratedRegex(@"^week(\d{2,})$")]
    private static partial Regex WeekIdRegex();
}

public static class BranchNames
{
    public const string DemoPrefix = "demo/";
    public const string RescuePrefix = "rescue/";

    public static bool IsDemo(string? branch) => branch?.StartsWith(DemoPrefix, StringComparison.Ordinal) == true;
    public static bool IsRescue(string? branch) => branch?.StartsWith(RescuePrefix, StringComparison.Ordinal) == true;

    /// <summary>Demo and rescue branches are ignored by Propagate, the overview and Check.</summary>
    public static bool IsReserved(string? branch) => IsDemo(branch) || IsRescue(branch);

    public static string DemoFor(string snapshot) => DemoPrefix + snapshot;

    public static string SnapshotOfDemo(string demoBranch) => demoBranch[DemoPrefix.Length..];

    public static string NewRescueName() => RescuePrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss");
}
