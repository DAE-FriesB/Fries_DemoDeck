using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DemoDeck.Core.Model;
using DemoDeck.Core.Services;

namespace DemoDeck.App.Dialogs;

/// <summary>One put-aside entry (git stash) of one project.</summary>
public sealed class StashRow
{
    public required RepoState Repo { get; init; }
    public required StashInfo Stash { get; init; }
    public required string Heading { get; init; }
    public required string Message { get; init; }
    public required string Age { get; init; }

    public string Key => Repo.Path + "|" + Stash.Ref;
    public string Tooltip => $"{Repo.Name}  ·  {Stash.Ref}\n{Stash.Message}";
}

/// <summary>One file inside a stash.</summary>
public sealed record StashFileRow(string Path, string Folder, string FileName, string Badge, Brush BadgeBrush, Brush BadgeSoft, string KindText);

/// <summary>
/// The changes that were put aside (git stash) in every project of the course: see which files each one holds,
/// restore it into its project (only into a project without unsaved changes) or drop it for good.
/// </summary>
public partial class StashesWindow : Window
{
    private readonly RepoService _repos;
    private readonly IReadOnlyList<string> _repoPaths;
    private int _filesVersion;

    public StashesWindow(RepoService repos, IReadOnlyList<string> repoPaths)
    {
        InitializeComponent();
        _repos = repos;
        _repoPaths = repoPaths;
        Loaded += async (_, _) => await ReloadAsync();
        UpdateButtons();
    }

    /// <summary>Raised after a stash was restored or dropped, so the main window refreshes.</summary>
    public event Action? Changed;

    private StashRow? Selected => StashList.SelectedItem as StashRow;

    private async Task ReloadAsync(string? selectKey = null)
    {
        selectKey ??= Selected?.Key;
        var rows = new List<StashRow>();
        foreach (var path in _repoPaths)
        {
            var state = await _repos.ReadStateAsync(path);
            rows.AddRange(state.Stashes.Select(s => ToRow(state, s)));
        }
        rows = rows.OrderByDescending(r => r.Stash.Date ?? DateTimeOffset.MinValue).ToList();

        StashList.ItemsSource = rows;
        StashList.SelectedItem = rows.FirstOrDefault(r => r.Key == selectKey) ?? rows.FirstOrDefault();
        CountText.Text = rows.Count == 1 ? "1 PUT ASIDE" : $"{rows.Count} PUT ASIDE";
        NoStashesText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
        await ShowFilesAsync(Selected);
    }

    private StashRow ToRow(RepoState repo, StashInfo stash)
    {
        var where = stash.Branch ?? "no branch";
        var message = stash.Message.StartsWith(RepoService.StashPrefix)
            ? "Put aside before switching"
            : stash.Message;
        return new StashRow
        {
            Repo = repo,
            Stash = stash,
            Heading = _repoPaths.Count > 1 ? $"{repo.Name}  ·  {where}" : where,
            Message = message,
            Age = stash.Date is { } d ? DescribeAge(d) : "",
        };
    }

    private static string DescribeAge(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        var days = (DateTime.Today - local.Date).Days;
        var ago = days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 60 => $"{days} days ago",
            _ => $"{days / 30} months ago",
        };
        return $"{ago}  ·  {local:yyyy-MM-dd HH:mm}";
    }

    // ---- selection and files -------------------------------------------------------------

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        await ShowFilesAsync(Selected);
    }

    private void UpdateButtons()
    {
        var row = Selected;
        RestoreButton.IsEnabled = DropButton.IsEnabled = row != null;
        if (row == null)
        {
            HintText.Text = "";
            return;
        }
        HintText.Text = row.Repo.IsDirty
            ? $"{row.Repo.Name} has unsaved changes: save or throw them away before restoring."
            : row.Stash.Branch != null && row.Repo.CurrentBranch != row.Stash.Branch
                ? $"Put aside on {row.Stash.Branch}; {row.Repo.Name} is now on {row.Repo.CurrentBranch ?? "no branch"}."
                : $"Restore puts these changes back into {row.Repo.Name} as unsaved changes.";
    }

    private async Task ShowFilesAsync(StashRow? row)
    {
        var version = ++_filesVersion;
        if (row == null)
        {
            FilesTitle.Text = "";
            FileList.ItemsSource = null;
            FilesMessage.Text = StashList.Items.Count == 0 ? "Nothing put aside." : "Select an entry to see its files.";
            return;
        }

        FilesTitle.Text = row.Repo.Name;
        IReadOnlyList<string> lines;
        try
        {
            lines = await _repos.ListStashFilesAsync(row.Repo.Path, row.Stash.Ref);
        }
        catch (Exception ex)
        {
            lines = [];
            FilesMessage.Text = "Could not read the files: " + ex.Message;
        }
        if (version != _filesVersion)
            return; // another entry was selected meanwhile

        var files = lines.Select(ToFileRow).OfType<StashFileRow>().ToList();
        FileList.ItemsSource = files;
        FilesTitle.Text = (files.Count == 1 ? "1 file" : $"{files.Count} files") + $" in {row.Repo.Name}";
        FilesMessage.Text = files.Count == 0 ? "No files." : "";
    }

    /// <summary>A <c>git stash show --name-status</c> line: "M\tpath", "R100\told\tnew".</summary>
    private static StashFileRow? ToFileRow(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 2) return null;
        var path = parts[^1];
        var slash = path.LastIndexOf('/');
        var (badge, color, soft, text) = parts[0][0] switch
        {
            'A' => ("+", "Teal", "TealSoft", "New file"),
            'D' => ("−", "Coral", "CoralSoft", "Deleted"),
            'R' => ("R", "Peach", "SandSoft", "Renamed: " + parts[1]),
            _ => ("•", "Sand", "SandSoft", "Modified"),
        };
        return new StashFileRow(path, slash >= 0 ? path[..(slash + 1)] : "", slash >= 0 ? path[(slash + 1)..] : path,
            badge, Res(color), Res(soft), text);
    }

    // ---- actions -------------------------------------------------------------------------

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var repo = await _repos.ReadStateAsync(row.Repo.Path);
        if (repo.IsDirty)
        {
            Message.Show(this,
                $"{repo.Name} has unsaved changes. Save them or throw them away first, then restore.\n\n" +
                "Restoring on top of unsaved changes would mix the two.",
                "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (row.Stash.Branch != null && repo.CurrentBranch != row.Stash.Branch)
        {
            var answer = Message.Show(this,
                $"These changes were put aside on {row.Stash.Branch}, but {repo.Name} is now on {repo.CurrentBranch ?? "no branch"}.\n\n" +
                $"Restore them here anyway? To restore them where they came from, cancel and open {row.Stash.Branch} first.",
                "Restore", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        await RunAsync("Restore", async () =>
        {
            await _repos.RestoreStashAsync(repo.Path, row.Stash.Ref);
            HintText.Text = $"Restored into {repo.Name}. The changes are unsaved changes there now.";
        });
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Selected != null)
        {
            e.Handled = true;
            OnDrop(sender, e);
        }
    }

    private async void OnDrop(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var answer = Message.Show(this,
            $"Delete these put-aside changes of {row.Repo.Name} for good?\n\n  {row.Message}\n  {row.Age}\n\nThis cannot be undone.",
            "Drop", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        await RunAsync("Drop", async () =>
        {
            await _repos.DropStashAsync(row.Repo.Path, row.Stash.Ref);
            HintText.Text = $"Dropped from {row.Repo.Name}.";
        });
    }

    private async Task RunAsync(string title, Func<Task> action)
    {
        IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Message.Show(this, ex.Message + (title == "Restore"
                    ? "\n\nThe put-aside changes are kept. Files that were already put back stay as unsaved changes."
                    : ""),
                title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
        var hint = HintText.Text;
        await ReloadAsync();
        HintText.Text = hint;
        Changed?.Invoke();
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
