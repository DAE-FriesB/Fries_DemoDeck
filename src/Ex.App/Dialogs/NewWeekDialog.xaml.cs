using System.Windows;
using Ex.Core.Model;
using Ex.Core.Services;

namespace Ex.App.Dialogs;

public partial class NewWeekDialog : Window
{
    private readonly IReadOnlyList<RepoState> _repos;
    private string? _previousSolution;

    public NewWeekDialog(IReadOnlyList<RepoState> repos)
    {
        InitializeComponent();
        _repos = repos;
        RepoPanel.Visibility = repos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RepoCombo.ItemsSource = repos.Select(r => r.Name).ToList();
        RepoCombo.SelectedIndex = 0;
        WeekText.Text = CourseSession.NextWeekNumber(repos).ToString();
        EmptyRadio.IsChecked = false;
        UpdateStartOptions();
        Loaded += (_, _) => TitleText.Focus();
    }

    public RepoState? Repo => _repos.FirstOrDefault(r => r.Name == RepoCombo.SelectedItem as string);
    public int Week => int.TryParse(WeekText.Text.Trim(), out var w) ? w : 0;
    public string WeekTitle => TitleText.Text.Trim();

    /// <summary>The branch to start from, or null for an empty project.</summary>
    public string? Source => ContinueRadio.IsChecked == true ? _previousSolution : null;

    private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateStartOptions();

    private void UpdateStartOptions()
    {
        if (Repo is not { } repo || ContinueRadio == null)
            return;
        var previous = repo.ExerciseBranches
            .Where(b => b.Kind == BranchKind.Solution && b.Week < Week)
            .MaxBy(b => b.Week);
        _previousSolution = previous?.Name;
        ContinueRadio.Visibility = previous != null ? Visibility.Visible : Visibility.Collapsed;
        ContinueRadio.Content = previous != null ? $"The solution of week {previous.Week}" : "";
        if (previous != null && EmptyRadio.IsChecked != true)
            ContinueRadio.IsChecked = true;
        if (previous == null)
            EmptyRadio.IsChecked = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (Repo is not { } repo)
            error = "Choose a project.";
        else if (Week <= 0)
            error = "Enter a week number (1, 2, ...).";
        else if (repo.ExerciseBranches.Any(b => b.Week == Week))
            error = $"Week {Week} already exists.";
        else if (repo.HasBranch(ExerciseBranch.WeekIdFor(Week)))
            error = $"There is a branch called '{ExerciseBranch.WeekIdFor(Week)}' in the project; rename it first.";
        if (error != null)
        {
            Message.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
