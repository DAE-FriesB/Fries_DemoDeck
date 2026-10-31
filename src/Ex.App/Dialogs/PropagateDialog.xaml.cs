using System.Windows;
using System.Windows.Media;
using Ex.Core.Services;

namespace Ex.App.Dialogs;

/// <summary>Shows what Propagate is about to merge, with the conflicts git predicts (computed in memory).</summary>
public partial class PropagateDialog : Window
{
    private sealed record Row(string Name, string Note, Brush NoteBrush, Brush Dot);

    public PropagateDialog(PropagatePlan plan, int unsynced, IReadOnlyDictionary<string, IReadOnlyList<string>> predicted,
        Func<string, string> describe)
    {
        InitializeComponent();
        var source = describe(plan.Source);
        HeadingText.Text = $"Propagate {source}";
        IntroText.Text = (unsynced == 1 ? "1 save" : $"{unsynced} saves") +
                         $" on {source} {(unsynced == 1 ? "is" : "are")} not in the steps after it yet. " +
                         "Propagate merges them forward, so every later step and solution has them too. Nothing is rewritten.";

        var rows = plan.Steps.Select(step =>
        {
            var conflicts = predicted.TryGetValue(step.Into, out var c) ? c : [];
            return conflicts.Count == 0
                ? new Row(describe(step.Into), "Merges cleanly", Res("TextMuted"), Res("Teal"))
                : new Row(describe(step.Into), "Both changed: " + string.Join(", ", conflicts) + "  (you choose which version to keep)",
                    Res("Peach"), Res("Peach"));
        }).ToList();
        StepList.ItemsSource = rows;
        ConflictHint.Visibility = rows.Any(r => r.Dot == Res("Peach")) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
