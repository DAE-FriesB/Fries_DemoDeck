using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ex.Core.Model;
using Ex.Core.Services;

namespace Ex.App.Dialogs;

/// <summary>One changed file in the list.</summary>
public sealed class ChangeRow
{
    public required string Repo { get; init; }
    public required string RepoPath { get; init; }
    public required StatusEntry Entry { get; init; }
    public required string Folder { get; init; }
    public required string FileName { get; init; }
    public required string Badge { get; init; }
    public required Brush BadgeBrush { get; init; }
    public required Brush BadgeSoft { get; init; }
    public required string KindText { get; init; }

    public string Key => Repo + "|" + Entry.Path;
    public string FullPath => Path.Combine(RepoPath, Entry.CurrentPath.Replace('/', Path.DirectorySeparatorChar));
}

public sealed record DiffLine(string Text, Brush Foreground, Brush Background);

/// <summary>
/// The list of unsaved changes, like GitHub Desktop: select one file to see its diff, select several
/// (Ctrl / Shift / Ctrl+A) to discard them together. Stays open and follows changes made in Unity or the IDE.
/// </summary>
public partial class ChangesWindow : Window
{
    private const int MaxDiffLines = 5000;

    private readonly RepoService _repos;
    private readonly IReadOnlyList<string> _repoPaths;
    private bool _reloading;
    private bool _reloadPending;
    private string? _diffKey;
    private int _diffVersion;

    public ChangesWindow(RepoService repos, IReadOnlyList<string> repoPaths, string subtitle)
    {
        InitializeComponent();
        _repos = repos;
        _repoPaths = repoPaths;
        SubtitleText.Text = subtitle;
        Loaded += async (_, _) => await ReloadAsync();
        UpdateSelectionUi();
    }

    /// <summary>Raised after files were discarded, so the main window refreshes.</summary>
    public event Action? Discarded;

    public IReadOnlyList<string> RepoPaths => _repoPaths;

    private List<ChangeRow> SelectedRows => FileList.SelectedItems.Cast<ChangeRow>().ToList();

    /// <summary>Re-reads the status, keeping the selection and the diff that is shown.</summary>
    public async Task ReloadAsync()
    {
        if (_reloading)
        {
            _reloadPending = true;
            return;
        }
        _reloading = true;
        try
        {
            do
            {
                _reloadPending = false;
                var rows = new List<ChangeRow>();
                foreach (var path in _repoPaths)
                {
                    var state = await _repos.ReadStateAsync(path);
                    rows.AddRange(state.Status.OrderBy(e => e.CurrentPath, StringComparer.OrdinalIgnoreCase).Select(e => ToRow(state, e)));
                }
                ShowRows(rows);
            }
            while (_reloadPending);
        }
        finally
        {
            _reloading = false;
        }
    }

    private void ShowRows(List<ChangeRow> rows)
    {
        var selected = SelectedRows.Select(r => r.Key).ToHashSet();
        FileList.ItemsSource = rows;
        foreach (var row in rows.Where(r => selected.Contains(r.Key)))
            FileList.SelectedItems.Add(row);
        if (FileList.SelectedItems.Count == 0 && rows.Count > 0)
            FileList.SelectedIndex = 0;

        CountText.Text = rows.Count == 1 ? "1 CHANGED FILE" : $"{rows.Count} CHANGED FILES";
        NoChangesText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionUi();

        // The file under the diff may have changed on disk: show it again.
        var focused = rows.FirstOrDefault(r => r.Key == _diffKey) ?? FileList.SelectedItem as ChangeRow;
        _ = ShowDiffAsync(focused);
    }

    private ChangeRow ToRow(RepoState repo, StatusEntry entry)
    {
        var path = entry.CurrentPath;
        var slash = path.LastIndexOf('/');
        var (badge, color, soft, text) = entry.Kind switch
        {
            ChangeKind.Untracked or ChangeKind.Added => ("+", "Teal", "TealSoft", "New file"),
            ChangeKind.Deleted => ("−", "Coral", "CoralSoft", "Deleted"),
            ChangeKind.Renamed => ("R", "Peach", "SandSoft", "Renamed: " + entry.Path),
            _ => ("•", "Sand", "SandSoft", "Modified"),
        };
        return new ChangeRow
        {
            Repo = repo.Name,
            RepoPath = repo.Path,
            Entry = entry,
            Folder = (_repoPaths.Count > 1 ? repo.Name + "/" : "") + (slash >= 0 ? path[..(slash + 1)] : ""),
            FileName = slash >= 0 ? path[(slash + 1)..] : path,
            Badge = badge,
            BadgeBrush = Res(color),
            BadgeSoft = Res(soft),
            KindText = text,
        };
    }

    // ---- selection and diff --------------------------------------------------------------

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionUi();
        // Show the file that was clicked last.
        var row = e.AddedItems.Count > 0 ? e.AddedItems[^1] as ChangeRow : FileList.SelectedItem as ChangeRow;
        if (row != null && row.Key != _diffKey)
            await ShowDiffAsync(row);
        else if (FileList.SelectedItems.Count == 0)
            await ShowDiffAsync(null);
    }

    private void UpdateSelectionUi()
    {
        var count = FileList.SelectedItems.Count;
        var label = count <= 1 ? "Discard changes..." : $"Discard {count} changes...";
        DiscardButton.Content = label;
        DiscardMenuItem.Header = label;
        DiscardButton.IsEnabled = DiscardMenuItem.IsEnabled = count > 0;
    }

    private async Task ShowDiffAsync(ChangeRow? row)
    {
        var version = ++_diffVersion;
        _diffKey = row?.Key;
        if (row == null)
        {
            DiffTitle.Text = "";
            DiffList.ItemsSource = null;
            DiffMessage.Text = FileList.Items.Count == 0 ? "Everything is saved." : "Select a file to see what changed.";
            return;
        }

        DiffTitle.Text = row.Entry.Kind == ChangeKind.Renamed ? row.Entry.Path : (_repoPaths.Count > 1 ? row.Repo + "/" : "") + row.Entry.CurrentPath;
        string? diff;
        try
        {
            diff = await _repos.DiffFileAsync(row.RepoPath, row.Entry);
        }
        catch (Exception ex)
        {
            diff = "(could not read the diff: " + ex.Message + ")";
        }
        if (version != _diffVersion)
            return; // another file was selected meanwhile

        if (diff == null)
        {
            DiffList.ItemsSource = null;
            DiffMessage.Text = "Binary file: no text diff to show.";
            return;
        }
        if (diff.Length == 0)
        {
            DiffList.ItemsSource = null;
            DiffMessage.Text = row.Entry.Kind == ChangeKind.Deleted ? "This file was deleted." : "No text changes (only the file mode or line endings changed).";
            return;
        }
        DiffMessage.Text = "";
        var lines = diff.Split('\n');
        var items = lines.Take(MaxDiffLines).Select(ToDiffLine).ToList();
        if (lines.Length > MaxDiffLines)
            items.Add(new DiffLine($"... {lines.Length - MaxDiffLines} more lines", Res("TextMuted"), Brushes.Transparent));
        DiffList.ItemsSource = items;
        if (items.Count > 0)
            DiffList.ScrollIntoView(items[0]);
    }

    private static DiffLine ToDiffLine(string line)
    {
        if (line.StartsWith("@@"))
            return new DiffLine(line, Res("TextMuted"), Res("Bg2"));
        if (line.StartsWith('+'))
            return new DiffLine(line, Res("Text"), Res("TealSoft"));
        if (line.StartsWith('-'))
            return new DiffLine(line, Res("Text"), Res("CoralSoft"));
        if (line.StartsWith('\\'))
            return new DiffLine(line, Res("TextMuted"), Brushes.Transparent);
        return new DiffLine(line, Res("TextSecondary"), Brushes.Transparent);
    }

    // ---- actions -------------------------------------------------------------------------

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        FileList.SelectAll();
        FileList.Focus();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && FileList.SelectedItems.Count > 0)
        {
            e.Handled = true;
            OnDiscard(sender, e);
        }
    }

    private async void OnDiscard(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;

        var names = rows.Take(12).Select(r => "  " + (_repoPaths.Count > 1 ? r.Repo + "/" : "") + r.Entry.CurrentPath).ToList();
        if (rows.Count > 12) names.Add($"  ... and {rows.Count - 12} more");
        var text = (rows.Count == 1 ? "Discard the changes to this file?" : $"Discard the changes to these {rows.Count} files?") +
                   "\n\n" + string.Join("\n", names) +
                   "\n\nModified files go back to their last saved version, new files are deleted. This cannot be undone.";
        if (Message.Show(this, text, "Discard changes", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        IsEnabled = false;
        try
        {
            foreach (var group in rows.GroupBy(r => r.RepoPath))
                await _repos.DiscardFilesAsync(group.Key, group.Select(r => r.Entry));
        }
        catch (Exception ex)
        {
            Message.Show(this, ex.Message, "Discard changes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
        FileList.SelectedItems.Clear();
        await ReloadAsync();
        Discarded?.Invoke();
    }

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ChangeRow row) return;
        var full = row.FullPath;
        if (File.Exists(full))
            Process.Start("explorer.exe", $"/select,\"{full}\"");
        else if (Path.GetDirectoryName(full) is { } dir && Directory.Exists(dir))
            Process.Start("explorer.exe", $"\"{dir}\"");
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
