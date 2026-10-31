using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ex.Core.Model;
using Ex.Core.Services;

namespace Ex.App.Dialogs;

/// <summary>
/// Shown before a switch when the working state is dirty (Preparation), and always when entering Demo mode.
/// The user chooses how the state is cleaned: Commit, Stash, Discard (with an "I understand" checkbox) or Cancel.
/// </summary>
public partial class CleanStateDialog : Window
{
    private readonly bool _demo;

    public CleanStateDialog(string heading, string details, IReadOnlyList<RepoState> dirtyRepos, bool enteringDemo)
    {
        InitializeComponent();
        _demo = enteringDemo;
        var dirty = dirtyRepos.Count > 0;

        Title = enteringDemo ? "Enter Demo mode" : "Uncommitted changes";
        HeadingText.Text = heading;
        DetailsText.Text = details;
        DemoPanel.Visibility = enteringDemo ? Visibility.Visible : Visibility.Collapsed;
        DirtyPanel.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;

        if (dirty)
        {
            var sb = new StringBuilder();
            foreach (var repo in dirtyRepos)
            {
                sb.AppendLine($"{repo.Name}  ({repo.CurrentBranch ?? "detached"})");
                foreach (var entry in repo.Status)
                    sb.AppendLine($"   {entry}");
            }
            FilesText.Text = sb.ToString();
        }
        else if (enteringDemo)
        {
            ProceedButton.Content = "Enter Demo mode";
            ProceedButton.Visibility = Visibility.Visible;
        }
        UpdateButtons();
    }

    /// <summary>The chosen way to clean; null when nothing needed cleaning (clean state, entering Demo).</summary>
    public CleanChoice? Choice { get; private set; }

    public string CommitMessageText => CommitMessage.Text.Trim();

    private void UpdateButtons()
    {
        var demoOk = !_demo || UnderstandDemo.IsChecked == true;
        CommitButton.IsEnabled = demoOk && CommitMessage.Text.Trim().Length > 0;
        StashButton.IsEnabled = demoOk;
        DiscardButton.IsEnabled = demoOk && UnderstandDiscard.IsChecked == true;
        ProceedButton.IsEnabled = demoOk;
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) => UpdateButtons();

    private void OnCommitMessageChanged(object sender, TextChangedEventArgs e) => UpdateButtons();

    private void OnCommit(object sender, RoutedEventArgs e) => Finish(CleanChoice.Commit);

    private void OnStash(object sender, RoutedEventArgs e) => Finish(CleanChoice.Stash);

    private void OnDiscard(object sender, RoutedEventArgs e) => Finish(CleanChoice.Discard);

    private void OnProceed(object sender, RoutedEventArgs e) => Finish(null);

    private void Finish(CleanChoice? choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
