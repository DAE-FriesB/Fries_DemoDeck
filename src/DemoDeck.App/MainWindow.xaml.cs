using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DemoDeck.App.Dialogs;
using DemoDeck.Core.Course;
using DemoDeck.Core.Git;
using DemoDeck.Core.Model;
using DemoDeck.Core.Services;
using Microsoft.Win32;

namespace DemoDeck.App;

public sealed record LogLine(LogKind Kind, string Text, Brush Brush);

public sealed record WeekRow(string Id, string Name, string Title, string Summary, bool IsCurrent);

/// <summary>A step card. In Preparation it has an Open button; in Demo mode the whole card is clickable.</summary>
public sealed record StepRow(
    string Branch, string Label, string Caption, bool IsCurrent, bool ShowOpen, Cursor? CardCursor,
    Brush NodeBrush, Brush NodeSoft, Brush CardBrush, bool HasLineAbove, bool HasLineBelow, int Unsynced, bool IsDemo,
    string ProjectsText = "", string StepPrefix = "", string EditTitle = "", bool CanRename = false, bool IsEditing = false)
{
    /// <summary>Which projects have this step; only shown when the course has more than one project.</summary>
    public bool ShowProjects => ProjectsText.Length > 0;
    public bool ShowLabel => !IsEditing;
    public bool ShowRename => CanRename && !IsEditing;
    public bool ShowUnsynced => Unsynced > 0 && !IsDemo;
    public string UnsyncedText => Unsynced == 1 ? "1 save not in later steps" : $"{Unsynced} saves not in later steps";
}

public partial class MainWindow : Window
{
    private const int MaxLogLines = 5000;

    private readonly GitRunner _git = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<LogLine> _log = [];
    private CourseSession? _session;
    private bool _busy;
    private string? _busyStatus;
    private bool _suppressSelection;
    private string? _weekToSelect;
    private ProjectWatcher? _watcher;
    private bool _quietRefreshing;
    private bool _refreshPending;
    private ChangesWindow? _changesWindow;
    private readonly Updates _updates = new();
    /// <summary>Branch of the step whose title is being edited (Preparation only), or null.</summary>
    private string? _renamingStep;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
        _git.Logged += OnLogged;
        Loaded += OnLoaded;
        PreviewKeyDown += OnWindowKeyDown;
        // Like a git client: pick up changes made in Unity, the IDE or the command line.
        Activated += async (_, _) => await QuietRefreshAsync();
    }

    private ClientMode Mode => _session?.Mode ?? ClientMode.Preparation;

    private bool IsDemo => Mode == ClientMode.Demo;

    private WeekOverview? SelectedWeek =>
        WeekList.SelectedItem is WeekRow row ? _session?.Weeks.FirstOrDefault(w => w.Id == row.Id) : null;

    // ---- startup and course folders -------------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildAll();
        var version = await _git.GetVersionAsync();
        if (version == null)
        {
            Message.Show(this, "git could not be started. Install Git for Windows and make sure 'git' is on the PATH.",
                "DemoDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _git.Info($"Using {version}");
        _ = CheckForUpdatesAsync();

        // A course folder on the command line wins over the most recent one (and is not added to the recent list).
        // A path that is not a course folder shows the start page.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            if (CourseFolder.HasManifest(args[1]))
                await OpenCourseAsync(args[1], remember: false);
            return;
        }
        var last = _settings.RecentFolders.FirstOrDefault(f => Directory.Exists(f) && CourseFolder.HasManifest(f));
        if (last != null)
            await OpenCourseAsync(last);
    }

    private void OnOpenMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = OpenButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var browse = new MenuItem { Header = "Open course folder..." };
        browse.Click += async (_, _) => await BrowseCourseAsync();
        menu.Items.Add(browse);
        if (_session != null && !IsDemo)
        {
            // Every project of the course (a Unity project, a console app...) is its own repository in Projects\.
            var addProject = new MenuItem { Header = "Add project..." };
            addProject.Click += async (_, _) => await CreateProjectAsync();
            menu.Items.Add(addProject);

            var stashes = _session.States.Sum(s => s.Stashes.Count);
            var putAside = new MenuItem { Header = stashes == 0 ? "Put-aside changes..." : $"Put-aside changes ({stashes})..." };
            putAside.Click += (_, _) => ShowStashes();
            menu.Items.Add(putAside);
        }
        if (_settings.RecentFolders.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var folder in _settings.RecentFolders)
            {
                var item = new MenuItem { Header = folder.Replace("_", "__"), IsEnabled = Directory.Exists(folder) };
                item.Click += async (_, _) => await OpenCourseAsync(folder);
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    private async void OnBrowseCourse(object sender, RoutedEventArgs e) => await BrowseCourseAsync();

    private async Task BrowseCourseAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open course folder (the folder with exercises.yaml and Projects\\)",
            InitialDirectory = _settings.RecentFolders.FirstOrDefault(Directory.Exists) ?? "",
        };
        if (dialog.ShowDialog(this) == true)
            await OpenCourseAsync(dialog.FolderName);
    }

    private async Task OpenCourseAsync(string folder, bool remember = true)
    {
        if (_busy) return;
        if (!CourseFolder.HasManifest(folder))
        {
            var answer = Message.Show(this,
                $"There is no {ManifestStore.FileName} in\n{folder}\n\nMake this a course folder (creates {ManifestStore.FileName} and a Projects folder)?",
                "Open course", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;
            CourseFolder.Create(folder, new CourseManifest { Course = new CourseInfo { Name = Path.GetFileName(folder) } });
        }

        CourseFolder course;
        try
        {
            course = CourseFolder.Open(folder);
        }
        catch (Exception ex)
        {
            Message.Show(this, $"Could not read {ManifestStore.FileName}:\n{ex.Message}", "Open course", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _session = new CourseSession(course, _git);
        _watcher?.Dispose();
        _watcher = new ProjectWatcher(course.ProjectsPath);
        _watcher.Changed += () => Dispatcher.BeginInvoke(async () => await QuietRefreshAsync());
        if (remember)
            _settings.AddRecent(course.Folder);
        _git.Info($"Opened course folder {course.Folder}");
        WeekList.SelectedItem = null;
        _weekToSelect = null;
        await RunAsync("Reading projects", () => Task.CompletedTask);
        await FixModeMismatchAsync();
    }

    /// <summary>Repos left on demo/... branches (after a crash) are returned to their real branch.</summary>
    private async Task FixModeMismatchAsync()
    {
        if (_session?.DescribeModeMismatch() is not { } mismatch)
            return;
        var answer = Message.Show(this,
            mismatch + "\n\nReturn every project to its normal branch now? Changes made during the demo are discarded; " +
            "commits made during the demo are kept on a rescue/ branch.",
            "Leftover demo state", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await RunAsync("Restoring", async () => await _session.LeaveDemoAsync());
    }

    // ---- busy handling and refresh ---------------------------------------------------------

    /// <summary>
    /// Re-reads the repos in the background of the UI: nothing is disabled and fields being edited keep their text.
    /// Skipped while an action runs (that action refreshes at the end anyway).
    /// </summary>
    private async Task QuietRefreshAsync()
    {
        if (_session == null)
            return;
        if (_busy || _quietRefreshing)
        {
            _refreshPending = true;
            return;
        }
        _quietRefreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                await _session.RefreshAsync();
            }
            while (_refreshPending && !_busy);
            if (!_busy)
                RebuildAll();
            if (_changesWindow != null)
                await _changesWindow.ReloadAsync();
        }
        catch (Exception ex)
        {
            _git.Error(ex.Message);
        }
        finally
        {
            _quietRefreshing = false;
        }
    }

    /// <summary>Runs an action with the UI disabled, reports errors, then re-reads all repos.</summary>
    private async Task RunAsync(string status, Func<Task> action)
    {
        if (_session == null || _busy)
            return;
        _busy = true;
        MainArea.IsEnabled = false;
        OpenButton.IsEnabled = false;
        _busyStatus = status + "...";
        StatusText.Text = _busyStatus;
        StatusDot.Fill = Res("Sand");
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _git.Error(ex.Message);
            Message.Show(this, ex.Message, status, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try
            {
                await _session.RefreshAsync();
            }
            catch (Exception ex)
            {
                _git.Error(ex.Message);
            }
            RebuildAll();
            if (StatusText.Text == _busyStatus)
                StatusText.Text = "Ready";
            _busy = false;
            StatusDot.Fill = Res("Teal");
            MainArea.IsEnabled = true;
            OpenButton.IsEnabled = true;
            if (_changesWindow != null)
                await _changesWindow.ReloadAsync();
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RunAsync("Refreshing", () => Task.CompletedTask);
        }
        // Presenter clickers send PageDown / PageUp.
        else if (IsDemo && e.Key == Key.PageDown)
        {
            e.Handled = true;
            await DemoStepAsync(+1);
        }
        else if (IsDemo && e.Key == Key.PageUp)
        {
            e.Handled = true;
            await DemoStepAsync(-1);
        }
    }

    private void RebuildAll()
    {
        ApplyModeUi();
        CourseText.Text = _session?.Course.DisplayName ?? "";
        Title = "DemoDeck" + (IsDemo ? "  -  DEMO MODE" : "") + (_session != null ? $"  -  {_session.Course.DisplayName}" : "");

        var hasCourse = _session != null;
        var hasProject = _session?.States.Count > 0;
        NoCoursePanel.Visibility = hasCourse ? Visibility.Collapsed : Visibility.Visible;
        NoProjectPanel.Visibility = hasCourse && !hasProject ? Visibility.Visible : Visibility.Collapsed;
        CoursePanel.Visibility = hasProject ? Visibility.Visible : Visibility.Collapsed;
        if (_session != null)
            NoProjectText.Text = $"There is no project in {_session.Course.ProjectsPath} yet. Create one to start adding weeks.";

        RebuildWeekList();
        ShowSelectedWeek();
    }

    // ---- weeks list ----------------------------------------------------------------------

    /// <summary>The branches the projects are on (for a demo branch: the snapshot it started from).</summary>
    private HashSet<string> CurrentBranches() =>
        (_session?.States ?? [])
            .Select(s => s.CurrentBranch)
            .OfType<string>()
            .Select(b => BranchNames.IsDemo(b) ? BranchNames.SnapshotOfDemo(b) : b)
            .ToHashSet();

    private static string WeekName(WeekOverview week) => $"Week {week.Week}";

    private void RebuildWeekList()
    {
        var selected = _weekToSelect ?? (WeekList.SelectedItem as WeekRow)?.Id;
        _weekToSelect = null;
        var current = CurrentBranches();
        var rows = (_session?.Weeks ?? []).Select(w =>
        {
            var steps = w.Entries.Count(e => e.EntryId.StartsWith("step-"));
            var projects = w.Entries.SelectMany(e => e.Repos).Distinct().Count();
            var origin = w.Continuation.Count == 0 ? ""
                : w.Continuation.Values.All(v => v) ? "  ·  continues"
                : w.Continuation.Values.All(v => !v) ? "  ·  fresh start"
                : "";   // projects start differently; the week page says how, per project
            var summary = w.OnlyInManifest ? "no branches yet"
                : (steps == 0 ? "no demo steps" : steps == 1 ? "1 demo step" : $"{steps} demo steps")
                  + (projects > 1 ? $"  ·  {projects} projects" : "") + origin;
            return new WeekRow(w.Id, WeekName(w).ToUpperInvariant(), w.Title ?? "Untitled week", summary, w.Entries.Any(e => current.Contains(e.BranchName)));
        }).ToList();

        _suppressSelection = true;
        WeekList.ItemsSource = rows;
        WeekList.SelectedItem = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault(r => r.IsCurrent) ?? rows.LastOrDefault();
        _suppressSelection = false;
        NoWeeksText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWeekSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        _renamingStep = null;
        ShowSelectedWeek();
    }

    private async void OnNewWeek(object sender, RoutedEventArgs e)
    {
        if (_session == null || _session.States.Count == 0) return;
        var dialog = new NewWeekDialog(_session.States) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Repo is not { } repo) return;
        var week = dialog.Week;
        await RunAsync($"Creating week {week}", async () =>
        {
            if (!await EnsureSavedAsync([repo], $"Create week {week}"))
                return;
            if (!ConfirmUnityOpen([repo]))
                return;
            await _session.NewWeekAsync(repo, week, dialog.Source, dialog.WeekTitle);
            _weekToSelect = ExerciseBranch.WeekIdFor(week);
            SetStatus($"Created week {week}. You are working on its solution.");
        });
    }

    // ---- selected week -------------------------------------------------------------------

    private void ShowSelectedWeek()
    {
        var week = SelectedWeek;
        NoWeekSelectedText.Visibility = week == null ? Visibility.Visible : Visibility.Collapsed;
        WeekPanel.Visibility = week == null ? Visibility.Collapsed : Visibility.Visible;
        if (week == null || _session == null)
            return;

        WeekNameText.Text = WeekName(week).ToUpperInvariant();
        // Never overwrite what is being typed (an automatic refresh can arrive at any moment).
        if (!WeekTitleBox.IsKeyboardFocusWithin)
            WeekTitleBox.Text = week.Title ?? "";
        if (!WeekNotesBox.IsKeyboardFocusWithin)
            WeekNotesBox.Text = week.Manifest?.Notes ?? "";
        WeekOriginText.Text = DescribeOrigin(week);
        ShowProjectsRow(week);

        var demo = IsDemo;
        var current = CurrentBranches();
        var last = week.Entries.Count - 1;
        var manyProjects = _session.States.Count > 1;
        if (demo || week.Entries.All(e => e.BranchName != _renamingStep))
            _renamingStep = null;
        // While a step title is being typed, an automatic refresh must not rebuild the cards (that would drop the text).
        if (_renamingStep == null)
        {
            StepList.ItemsSource = week.Entries.Select((e, i) =>
            {
                var isCurrent = current.Contains(e.BranchName);
                var isStep = e.EntryId.StartsWith("step-");
                var prefix = isStep ? $"Step {e.EntryId[5..]}" : "";
                var (label, caption, color) = e.EntryId switch
                {
                    "start" => ("Start", "What students start from", "Teal"),
                    "solution" => ("Solution", demo ? "The finished exercise" : "The finished exercise. This is where you work.", "Coral"),
                    _ => (prefix + (string.IsNullOrWhiteSpace(e.Title) ? "" : $"  ·  {e.Title}"), "Demo point to jump to in class", "Sand"),
                };
                var soft = color == "Sand" ? "SandSoft" : color == "Teal" ? "TealSoft" : "CoralSoft";
                return new StepRow(e.BranchName, label, caption, isCurrent, !isCurrent && !demo,
                    demo && !isCurrent ? Cursors.Hand : null,
                    Res(color), Res(soft), Res(isCurrent ? "Bg2" : "Bg1"), i > 0, i < last, e.Unsynced, demo,
                    manyProjects ? string.Join("  ·  ", e.Repos) : "",
                    prefix, e.Title ?? "", isStep && !demo, false);
            }).ToList();
        }

        if (demo)
        {
            WorkPanel.Visibility = Visibility.Collapsed;
            NotWorkingText.Visibility = Visibility.Collapsed;
            ShowDemoPanel(week);
            return;
        }
        DemoPanel.Visibility = Visibility.Collapsed;

        // The work panel only appears while one of this week's branches is checked out.
        var working = WorkingRepos(week);
        WorkPanel.Visibility = working.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotWorkingText.Visibility = working.Count == 0 && !week.OnlyInManifest ? Visibility.Visible : Visibility.Collapsed;
        if (working.Count > 0)
        {
            var branch = working[0].CurrentBranch!;
            var changes = working.Sum(r => r.Status.Count);
            var onSolution = branch.EndsWith("/solution");
            WorkDot.Fill = Res(changes > 0 ? "Peach" : "Teal");
            var samePoint = working.All(r => r.CurrentBranch == branch);
            WorkText.Text = samePoint ? $"Working on {PointName(branch)}" : $"Working on week {week.Week}";
            if (working.Count > 1)
            {
                // Several projects in this week: show where each one is.
                WorkProjectsText.Text = string.Join("\n", working.Select(r =>
                    $"{r.Name}  ·  {PointName(r.CurrentBranch!)}  ·  " +
                    (r.Status.Count == 0 ? "saved" : r.Status.Count == 1 ? "1 change" : $"{r.Status.Count} changes")));
                WorkProjectsText.Visibility = Visibility.Visible;
            }
            else
            {
                WorkProjectsText.Visibility = Visibility.Collapsed;
            }
            ChangesButton.Content = changes == 1 ? "1 unsaved change" : $"{changes} unsaved changes";
            ChangesButton.Visibility = changes > 0 ? Visibility.Visible : Visibility.Collapsed;
            AllSavedText.Visibility = changes > 0 ? Visibility.Collapsed : Visibility.Visible;
            var stashes = working.Sum(r => r.Stashes.Count);
            StashesButton.Content = $"{stashes} put aside";
            StashesButton.Visibility = stashes > 0 ? Visibility.Visible : Visibility.Collapsed;
            var entry = week.Entries.FirstOrDefault(en => en.BranchName == branch);
            var unsynced = entry?.Unsynced ?? 0;
            var hasLater = week.Entries.LastOrDefault() is { } lastEntry && lastEntry.BranchName != branch;
            var where = working.Count > 1 ? $" in {JoinNames(working.Select(r => r.Name))}" : "";
            WorkHint.Text = onSolution
                ? $"Save stores your changes{where}. You can also mark the saved state as the next demo step."
                : $"Save stores your changes{where} on {DescribeBranch(branch)}.";

            // Working on an earlier point: say plainly that later steps will not have it.
            var saves = unsynced == 1 ? "1 save here is" : $"{unsynced} saves here are";
            var warning = (!onSolution && hasLater, unsynced > 0) switch
            {
                (true, true) => $"Saving here changes {DescribeBranch(branch)} only, not the solution. {saves} not in the later steps yet: Propagate carries them forward.",
                (true, false) => $"Saving here changes {DescribeBranch(branch)} only, not the solution. The later steps get it when you propagate.",
                (false, true) => $"{saves[0..1].ToUpperInvariant()}{saves[1..]} not in the following week yet: Propagate carries them forward.",
                _ => null,
            };
            WorkWarning.Text = warning ?? "";
            WorkWarning.Visibility = warning != null ? Visibility.Visible : Visibility.Collapsed;
            PropagateButton.Visibility = unsynced > 0 ? Visibility.Visible : Visibility.Collapsed;
            PropagateButton.IsEnabled = changes == 0;
            PropagateButton.ToolTip = changes == 0
                ? "Carry the saves on this step forward into the later steps and the solution"
                : "Save (or throw away) your changes first";
            SaveButton.IsEnabled = changes > 0 || onSolution;
        }
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private string DescribeOrigin(WeekOverview week)
    {
        if (week.OnlyInManifest)
            return "This week is in exercises.yaml but has no branches yet.";
        if (week.Continuation.Count == 0)
            return "";
        if (week.Continuation.Values.Distinct().Count() > 1)
        {
            // Projects of the same week can start differently: say it per project.
            return string.Join("   ", week.Continuation.Select(kv =>
                $"{kv.Key}: {(kv.Value ? $"continues week {PreviousWeekNumber(week)}" : "fresh start")}."));
        }
        return week.Continuation.Values.All(v => v)
            ? $"Continues from the solution of week {PreviousWeekNumber(week)}."
            : "Fresh start (does not continue the previous week).";
    }

    /// <summary>"the solution", "the start" or "step 2".</summary>
    private static string PointName(string branch) =>
        branch.EndsWith("/solution") ? "the solution"
        : branch.EndsWith("/start") ? "the start"
        : "step " + branch[(branch.LastIndexOf('-') + 1)..];

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string JoinNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list[..^1]) + " and " + list[^1];
    }

    /// <summary>Projects that have branches of this week, in course order.</summary>
    private List<RepoState> WeekRepos(WeekOverview week) =>
        _session!.States.Where(s => s.ExerciseBranches.Any(b => b.Week == week.Week)).ToList();

    /// <summary>One chip per project of the week, plus "+ project" when another project could join it.</summary>
    private void ShowProjectsRow(WeekOverview week)
    {
        ProjectsRow.Children.Clear();
        var many = _session!.States.Count > 1;
        ProjectsRow.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        if (!many)
            return;

        var inWeek = WeekRepos(week);
        foreach (var repo in inWeek)
        {
            var onWeek = repo.CurrentBranch is { } b
                         && ExerciseBranch.TryParse(BranchNames.IsDemo(b) ? BranchNames.SnapshotOfDemo(b) : b, out var eb)
                         && eb.Week == week.Week;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6),
                Background = Res(onWeek ? "AccentSoft" : "Bg2"),
                ToolTip = onWeek ? $"{repo.Name} is on {repo.CurrentBranch}" : $"{repo.Name} is on {repo.CurrentBranch ?? "no branch"} (another week)",
                Child = new TextBlock
                {
                    Text = repo.Name, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = Res(onWeek ? "Accent" : "TextSecondary"),
                },
            };
            ProjectsRow.Children.Add(chip);
        }

        if (!IsDemo && _session.States.Count > inWeek.Count)
        {
            var add = new Button
            {
                Content = "+ project", FontSize = 12, MinHeight = 24, Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 6), ToolTip = $"Add another project to week {week.Week}",
            };
            add.SetResourceReference(StyleProperty, "GhostButton");
            add.Click += async (_, _) => await AddProjectToWeekAsync(week.Week);
            ProjectsRow.Children.Add(add);
        }
    }

    /// <summary>Gives another project branches for an existing week (its own start and solution).</summary>
    private async Task AddProjectToWeekAsync(int week)
    {
        if (_session == null) return;
        var candidates = _session.States.Where(s => !s.ExerciseBranches.Any(b => b.Week == week)).ToList();
        if (candidates.Count == 0) return;
        var dialog = new NewWeekDialog(candidates, fixedWeek: week) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Repo is not { } repo) return;
        await RunAsync($"Adding {repo.Name} to week {week}", async () =>
        {
            if (!await EnsureSavedAsync([repo], $"Add {repo.Name} to week {week}"))
                return;
            if (!ConfirmUnityOpen([repo]))
                return;
            await _session.NewWeekAsync(repo, week, dialog.Source, null);
            _weekToSelect = ExerciseBranch.WeekIdFor(week);
            SetStatus($"Added {repo.Name} to week {week}. It is on its solution.");
        });
    }

    private int PreviousWeekNumber(WeekOverview week) =>
        _session!.Weeks.Where(w => w.Week < week.Week && w.Entries.Any(e => e.EntryId == "solution")).Select(w => w.Week).DefaultIfEmpty(week.Week - 1).Max();

    /// <summary>Projects that currently have a (real) branch of this week checked out.</summary>
    private List<RepoState> WorkingRepos(WeekOverview week) =>
        _session!.States
            .Where(s => s.CurrentBranch is { } b && ExerciseBranch.TryParse(b, out var eb) && eb.Week == week.Week)
            .ToList();

    private async void OnOpenStep(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StepRow row || _session == null) return;
        var branch = row.Branch;
        await RunAsync($"Opening {branch}", async () =>
        {
            var affected = _session.States.Where(s => s.HasBranch(branch) && s.CurrentBranch != branch).ToList();
            if (!await EnsureSavedAsync(affected, $"Open {branch}"))
                return;
            if (!ConfirmUnityOpen(affected))
                return;
            var report = await _session.SwitchPrepAsync(branch, null);
            ShowFailures(report, "Open");
            SetStatus($"Opened {branch}.");
        });
    }

    /// <summary>Opens (or brings forward) the list of changed files of the projects this week is checked out in.</summary>
    private void OnShowChanges(object sender, RoutedEventArgs e)
    {
        if (SelectedWeek is not { } week || _session == null) return;
        var working = WorkingRepos(week);
        if (working.Count == 0) return;

        var paths = working.Select(r => r.Path).ToList();
        if (_changesWindow != null && _changesWindow.RepoPaths.SequenceEqual(paths))
        {
            _changesWindow.Activate();
            return;
        }
        _changesWindow?.Close();
        var subtitle = string.Join("   ", working.Select(r => $"{r.Name}  ·  {r.CurrentBranch}"));
        _changesWindow = new ChangesWindow(_session.Repos, paths, subtitle) { Owner = this };
        _changesWindow.Discarded += async () => await QuietRefreshAsync();
        _changesWindow.Closed += (_, _) => _changesWindow = null;
        _changesWindow.Show();
    }

    private void OnShowStashes(object sender, RoutedEventArgs e) => ShowStashes();

    /// <summary>The changes put aside (git stash) in every project of the course: restore or drop them.</summary>
    private void ShowStashes()
    {
        if (_session == null || _busy || IsDemo) return;
        var window = new StashesWindow(_session.Repos, _session.States.Select(s => s.Path).ToList()) { Owner = this };
        window.Changed += async () => await QuietRefreshAsync();
        window.ShowDialog();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (SelectedWeek is not { } week || _session == null) return;
        var working = WorkingRepos(week);
        if (working.Count == 0) return;
        var branch = working[0].CurrentBranch!;
        var onSolution = branch.EndsWith("/solution");

        var large = working.SelectMany(r => _session.LargeUncommittedFiles(r)).ToList();
        if (large.Count > 0 && Message.Show(this,
                $"These files are larger than {_session.Manifest.Settings.LargeFileMb} MB. Large files stay in the project history forever:\n\n" +
                string.Join("\n", large.Select(f => $"{f.Path}  ({f.Size / 1024.0 / 1024.0:0.0} MB)")) + "\n\nSave anyway?",
                "Large files", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var nextStep = working.Select(r => CourseSession.NextStepNumber(r, week.Week)).Max();
        var dialog = new SaveDialog(working.Sum(r => r.Status.Count), onSolution ? nextStep : null,
            working.Count > 1 ? JoinNames(working.Select(r => r.Name)) : null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunAsync("Saving", async () =>
        {
            var step = await _session.SaveWorkAsync(working, week.Week, dialog.Message, dialog.MarkAsStep, dialog.StepTitle);
            SetStatus(step != null ? $"Saved and marked as step {nextStep}." : "Saved.");
        });
    }

    private void OnWeekTextLostFocus(object sender, RoutedEventArgs e)
    {
        if (_session == null || IsDemo || SelectedWeek is not { } week) return;
        var title = NullIfEmpty(WeekTitleBox.Text);
        var notes = NullIfEmpty(WeekNotesBox.Text);
        if (title == week.Manifest?.Title && notes == week.Manifest?.Notes) return;

        var entry = _session.Manifest.GetOrAddWeek(week.Id);
        entry.Title = title;
        entry.Notes = notes;
        week.Manifest = entry;
        _session.Save();
        RebuildWeekList();
        SetStatus($"Saved title and notes of {WeekName(week)}.");
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---- renaming a step -------------------------------------------------------------------

    /// <summary>Pencil button on a step card: swaps the name for a text field.</summary>
    private void OnRenameStep(object sender, RoutedEventArgs e)
    {
        if (IsDemo || (sender as FrameworkElement)?.DataContext is not StepRow row
            || StepList.ItemsSource is not IEnumerable<StepRow> rows) return;
        StepList.ItemsSource = rows.Select(r => r with { IsEditing = r.Branch == row.Branch }).ToList();
        _renamingStep = row.Branch;
    }

    private void OnStepTitleBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnStepTitleBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitStepRename(box);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _renamingStep = null;
            ShowSelectedWeek();
        }
    }

    private void OnStepTitleBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            CommitStepRename(box);
    }

    /// <summary>Stores the typed title in exercises.yaml (an empty title removes it). Branches are not renamed.</summary>
    private void CommitStepRename(TextBox box)
    {
        if (_renamingStep is not { } branch || box.DataContext is not StepRow row || row.Branch != branch) return;
        _renamingStep = null;
        if (_session != null && !IsDemo && SelectedWeek is { } week
            && week.Entries.FirstOrDefault(en => en.BranchName == branch) is { } entry)
        {
            var title = NullIfEmpty(box.Text);
            if (title != NullIfEmpty(entry.Title ?? ""))
            {
                week.Manifest = _session.RenameStep(week.Id, entry.EntryId, title);
                entry.Title = title;
                SetStatus(title == null
                    ? $"Removed the name of step {entry.EntryId[5..]} of {WeekName(week)}."
                    : $"Renamed step {entry.EntryId[5..]} of {WeekName(week)} to \"{title}\".");
            }
        }
        ShowSelectedWeek();
    }

    // ---- Propagate -------------------------------------------------------------------------

    /// <summary>"week01/step-2" as people say it: "step 2 of week 1", "the solution of week 1".</summary>
    private static string DescribeBranch(string branch) =>
        !ExerciseBranch.TryParse(branch, out var b) ? branch
        : b.Kind switch
        {
            BranchKind.Start => $"the start of week {b.Week}",
            BranchKind.Solution => $"the solution of week {b.Week}",
            _ => $"step {b.Step} of week {b.Week}",
        };

    private async void OnPropagateStep(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StepRow row)
            await PropagateAsync(row.Branch);
    }

    private async void OnPropagateCurrent(object sender, RoutedEventArgs e)
    {
        if (SelectedWeek is { } week && WorkingRepos(week).FirstOrDefault()?.CurrentBranch is { } branch)
            await PropagateAsync(branch);
    }

    /// <summary>
    /// Merges the saves of <paramref name="branch"/> forward into every later step, solution and following week
    /// (D12: merge, never rewrite). Shows the plan with predicted conflicts first; conflicts are settled per file.
    /// Afterwards every project is back on the branch it was on.
    /// </summary>
    private async Task PropagateAsync(string branch)
    {
        if (_session == null || IsDemo) return;
        await RunAsync($"Propagating {branch}", async () =>
        {
            var affected = _session.States.Where(s => s.HasBranch(branch)).ToList();
            if (!await EnsureSavedAsync(affected, $"Propagate {DescribeBranch(branch)}"))
                return;
            if (!ConfirmUnityOpen(affected))
                return;
            await _session.RefreshAsync();

            var updated = 0;
            foreach (var repo in _session.States.Where(s => s.HasBranch(branch)).ToList())
            {
                var returnTo = repo.CurrentBranch;
                var plan = await _session.Propagate.PlanAsync(repo, branch);
                if (plan.Steps.Count == 0)
                    continue;
                var predicted = await _session.Propagate.PredictConflictsAsync(plan);
                var unsynced = await _session.Repos.CountCommitsAsync(repo.Path, $"{plan.Steps[0].Into}..{branch}");
                var dialog = new PropagateDialog(plan, unsynced, predicted, DescribeBranch) { Owner = this };
                if (dialog.ShowDialog() != true)
                    return;

                var completed = await RunPlanAsync(plan);
                if (returnTo != null && (await _session.Repos.ReadStateAsync(repo.Path)).CurrentBranch != returnTo)
                    await _session.Repos.SwitchAsync(repo.Path, returnTo);
                if (!completed)
                {
                    SetStatus("Propagation stopped. The steps that were already updated stay updated.");
                    return;
                }
                updated += plan.Steps.Count;
            }
            SetStatus(updated == 0
                ? $"Nothing to propagate: everything after {DescribeBranch(branch)} already has its saves."
                : $"Propagated {DescribeBranch(branch)} into {updated} later step{(updated == 1 ? "" : "s")}.");
        });
    }

    /// <summary>Runs a plan, asking per conflicting file how to settle it. False when the user stopped it.</summary>
    private async Task<bool> RunPlanAsync(PropagatePlan plan)
    {
        var progress = await _session!.Propagate.RunAsync(new PropagateProgress { Plan = plan });
        while (progress.Status != PropagateStatus.Completed)
        {
            if (progress.Status == PropagateStatus.Failed)
            {
                Message.Show(this, $"Propagating into {DescribeBranch(progress.Current?.Into ?? "")} failed:\n\n{progress.Error}",
                    "Propagate", MessageBoxButton.OK, MessageBoxImage.Error);
                await _session.Propagate.AbortAsync(progress);
                return false;
            }

            var step = progress.Current!;
            var dialog = new ConflictDialog(progress.ConflictFiles, DescribeBranch(step.From), DescribeBranch(step.Into)) { Owner = this };
            dialog.ShowDialog();
            if (!dialog.Continue)
            {
                await _session.Propagate.AbortAsync(progress);
                return false;
            }
            await _session.Propagate.ResolveAsync(progress, dialog.Choices);
            progress = await _session.Propagate.ContinueAsync(progress);
            if (progress.Status == PropagateStatus.Conflict && progress.Current == step)
                Message.Show(this, "These files still contain conflict markers (<<<<<<< / >>>>>>>):\n\n" + string.Join("\n", progress.ConflictFiles),
                    "Propagate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return true;
    }

    // ---- Demo mode -------------------------------------------------------------------------

    /// <summary>Theme, header and which controls exist. Demo mode hides everything that changes the exercises.</summary>
    private void ApplyModeUi()
    {
        var demo = IsDemo;
        Theme.Apply(Mode);
        DemoBadge.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        ModeButton.Visibility = _session?.States.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModeButton.Content = demo ? "Leave demo" : "Demo mode  ▶";
        ModeButton.SetResourceReference(StyleProperty, demo ? "DangerButton" : "PrimaryButton");
        ModeButton.ToolTip = demo
            ? "Back to Preparation: demo changes are thrown away, you return to where you were"
            : "Present in class: jump between steps with one click";
        NewWeekButton.Visibility = demo ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.Visibility = demo ? Visibility.Collapsed : Visibility.Visible;
        WeekTitleBox.IsReadOnly = demo;
        WeekTitleBox.Focusable = !demo;
        WeekNotesBox.IsReadOnly = demo;
        if (demo && _changesWindow != null)
            _changesWindow.Close();
    }

    private void ShowDemoPanel(WeekOverview week)
    {
        DemoPanel.Visibility = Visibility.Visible;
        var current = DemoCurrentEntry();
        if (current == null || current.WeekId != week.Id)
        {
            DemoText.Text = "Click a step to jump to it";
            DemoHint.Text = "Every jump throws away what was changed during the demo. Unity's Library folder is kept.";
            return;
        }
        var changes = _session!.States.Where(s => s.IsOnDemoBranch).Sum(s => s.Status.Count);
        DemoText.Text = $"At {DescribeEntry(current)}";
        if (_session.States.Count > 1)
        {
            var onStep = _session.States.Where(s => s.CurrentBranch == BranchNames.DemoFor(current.BranchName)).Select(s => s.Name);
            DemoText.Text += $"  ·  {JoinNames(onStep)}";
        }
        DemoHint.Text = changes == 0
            ? "Nothing changed yet. Next / Previous (or PageDown / PageUp) move through the steps."
            : $"{changes} change{(changes == 1 ? "" : "s")} made during the demo: thrown away on the next jump or Reset step.";
        // Projects without this step stay where they were: say so, it is easy to miss on stage.
        var behind = _session.States
            .Where(s => s.IsOnDemoBranch && s.CurrentBranch != BranchNames.DemoFor(current.BranchName))
            .Select(s => $"{s.Name} has no {DescribeEntry(current).Split("  ·  ")[0]} and stays at {PointName(BranchNames.SnapshotOfDemo(s.CurrentBranch!))}.")
            .ToList();
        if (behind.Count > 0)
            DemoHint.Text = string.Join(" ", behind) + " " + DemoHint.Text;
    }

    private static string DescribeEntry(EntryOverview entry) => entry.EntryId switch
    {
        "start" => "the start",
        "solution" => "the solution",
        _ => $"step {entry.EntryId[5..]}" + (string.IsNullOrWhiteSpace(entry.Title) ? "" : $"  ·  {entry.Title}"),
    };

    /// <summary>
    /// The step the demo is at. With several projects they can be at different steps: a jump to a step that only
    /// some projects have leaves the others where they were. The current step is the one every project in Demo mode
    /// is either on or doesn't have.
    /// </summary>
    private EntryOverview? DemoCurrentEntry()
    {
        if (_session == null) return null;
        var onDemo = _session.States.Where(s => s.IsOnDemoBranch).ToList();
        if (onDemo.Count == 0) return null;
        var entries = _session.Weeks.SelectMany(w => w.Entries).ToList();
        var candidates = onDemo.Select(s => BranchNames.SnapshotOfDemo(s.CurrentBranch!)).Distinct()
            .Select(b => entries.FirstOrDefault(e => e.BranchName == b)).OfType<EntryOverview>().ToList();
        return candidates.FirstOrDefault(e => onDemo.All(s => s.CurrentBranch == BranchNames.DemoFor(e.BranchName) || !s.HasBranch(e.BranchName)))
               ?? candidates.FirstOrDefault();
    }

    private async void OnToggleMode(object sender, RoutedEventArgs e)
    {
        if (IsDemo)
            await LeaveDemoAsync();
        else
            await EnterDemoAsync();
    }

    /// <summary>
    /// Preparation to Demo. With everything saved this is one click (the window colour shows the mode).
    /// Unsaved changes are the only thing Demo mode could lose, so then the user decides first what happens to them.
    /// The mode is only saved when every project switched.
    /// </summary>
    private async Task EnterDemoAsync()
    {
        await RunAsync("Entering Demo mode", async () =>
        {
            await _session!.RefreshAsync();
            var dirty = _session.DirtyRepos.ToList();
            if (dirty.Count > 0)
            {
                var dialog = new CleanStateDialog("Enter Demo mode",
                    "There are unsaved changes. Deal with them first: they never come along into Demo mode.",
                    dirty, enteringDemo: true) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Choice is not { } choice)
                    return;
                await _session.CleanAsync(dirty, choice, dialog.CommitMessageText);
                await _session.RefreshAsync();
                if (_session.DirtyRepos.Any())
                {
                    Message.Show(this, "Not every project is clean. Demo mode was not started.", "Enter Demo mode",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            while (true)
            {
                var report = await _session.EnterDemoAsync();
                if (report.AllSucceeded)
                {
                    var unity = _session.States.Where(s => s.IsUnityOpen).Select(s => s.Name).ToList();
                    SetStatus("Demo mode. Click a step to jump to it." + (unity.Count > 0 ? $"  Unity has {string.Join(", ", unity)} open: let it refresh." : ""));
                    return;
                }
                var text = "Not every project could switch, so Demo mode was not started.\n\n" +
                           string.Join("\n", report.Outcomes.Select(o => $"{(o.Success ? "ok" : "FAILED")}  {o.Repo}: {o.Message}")) +
                           "\n\nYes = try again    No = put the projects that did switch back    Cancel = leave it like this";
                var answer = Message.Show(this, text, "Enter Demo mode", MessageBoxButton.YesNoCancel, MessageBoxImage.Error);
                if (answer == MessageBoxResult.Yes)
                    continue;
                if (answer == MessageBoxResult.No)
                    ShowFailures(await _session.RollBackEnterDemoAsync(), "Put back");
                return;
            }
        });
    }

    /// <summary>Demo to Preparation: one click, no dialog. Stray commits end up on a rescue/ branch.</summary>
    private async Task LeaveDemoAsync()
    {
        await RunAsync("Leaving Demo mode", async () =>
        {
            var report = await _session!.LeaveDemoAsync();
            var notices = new List<string> { report.AllSucceeded ? "Back in Preparation." : "Could not leave Demo mode completely." };
            if (report.Rescued.Count > 0)
                notices.Add($"Commits made during the demo were kept on {string.Join(", ", report.Rescued)}.");
            var unity = _session.States.Where(s => s.IsUnityOpen).Select(s => s.Name).ToList();
            if (unity.Count > 0)
                notices.Add($"Unity has {string.Join(", ", unity)} open: let it refresh.");
            SetStatus(string.Join("  ", notices));
            if (!report.AllSucceeded)
                Message.Show(this,
                    "These projects could not return to Preparation; Demo mode stays on. Fix the problem (for example a file locked by Unity) and click Leave demo again.\n\n" +
                    string.Join("\n", report.Failed.Select(f => $"{f.Repo}: {f.Message}")),
                    "Leave demo", MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    /// <summary>Jumps to a step: changes made during the demo are thrown away, no questions asked.</summary>
    private async Task DemoJumpAsync(string branch)
    {
        await RunAsync($"Jumping to {branch}", async () =>
        {
            var report = await _session!.DemoSwitchAsync(branch);
            var notices = new List<string> { $"At {branch}." };
            if (report.Rescued.Count > 0)
                notices.Add($"Commits made during the demo were kept on {string.Join(", ", report.Rescued)}.");
            var unity = _session.States.Where(s => s.HasBranch(branch) && s.IsUnityOpen).Select(s => s.Name).ToList();
            if (unity.Count > 0)
                notices.Add("Unity is open: let it refresh.");
            SetStatus(string.Join("  ", notices));
            ShowFailures(report, "Jump");
            if (ExerciseBranch.TryParse(branch, out var target))
                _weekToSelect = target.WeekId;
        });
    }

    private async void OnStepCardClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsDemo || (sender as FrameworkElement)?.DataContext is not StepRow row || row.IsCurrent) return;
        await DemoJumpAsync(row.Branch);
    }

    /// <summary>Next / previous step, across weeks.</summary>
    private async Task DemoStepAsync(int delta)
    {
        if (_busy || _session == null) return;
        var entries = _session.Weeks.SelectMany(w => w.Entries).ToList();
        if (entries.Count == 0) return;
        // Within the week on screen: the next/previous step. Looking at another week: start at its first
        // (Next) or last (Previous) step.
        var current = DemoCurrentEntry();
        var shown = SelectedWeek;
        int target;
        if (current != null && (shown == null || shown.Id == current.WeekId))
            target = entries.IndexOf(current) + delta;
        else if (shown is { Entries.Count: > 0 })
            target = entries.IndexOf(delta > 0 ? shown.Entries[0] : shown.Entries[^1]);
        else
            return;
        if (target < 0 || target >= entries.Count)
        {
            SetStatus(delta > 0 ? "This is the last step." : "This is the first step.");
            return;
        }
        await DemoJumpAsync(entries[target].BranchName);
    }

    private async void OnDemoNext(object sender, RoutedEventArgs e) => await DemoStepAsync(+1);

    private async void OnDemoPrevious(object sender, RoutedEventArgs e) => await DemoStepAsync(-1);

    private async void OnDemoReset(object sender, RoutedEventArgs e) =>
        await RunAsync("Resetting the step", async () =>
        {
            var report = await _session!.DemoResetAsync();
            SetStatus(report.Outcomes.Count == 0 ? "Click a step first." : "Back to the start of this step.");
            ShowFailures(report, "Reset step");
        });

    // ---- updates ---------------------------------------------------------------------------

    /// <summary>Checks GitHub for a newer release in the background; when one is downloaded, the header offers a restart.</summary>
    private async Task CheckForUpdatesAsync()
    {
        VersionText.Text = _updates.CurrentVersion is { } current ? $"v{current}" : "development build";
        var newVersion = await _updates.CheckAndDownloadAsync();
        if (newVersion == null)
            return;
        UpdateButton.Content = $"Version {newVersion} ready: restart";
        UpdateButton.Visibility = Visibility.Visible;
        _git.Info($"Version {newVersion} downloaded; restart to update.");
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            Message.Show(this, "Please wait until the current operation has finished.", "Update");
            return;
        }
        if (IsDemo && Message.Show(this, "You are in Demo mode. Restart now to update? The app reopens in Demo mode.",
                "Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _updates.RestartToUpdate();
    }

    // ---- project ---------------------------------------------------------------------------

    private async void OnCreateProject(object sender, RoutedEventArgs e) => await CreateProjectAsync();

    private async Task CreateProjectAsync()
    {
        if (_session == null) return;
        var projects = _session.Course.ProjectsPath;
        var first = _session.States.Count == 0;
        const string unity = "Unity project";
        const string dotnet = ".NET project (ASP.NET Core / console)";
        var dialog = new PromptDialog(first ? "Create project" : "Add project",
                $"Creates an empty git repository for the project in {projects}.\n" +
                (first
                    ? "You create the Unity or .NET project itself in the first week."
                    : "Then add it to a week: + for a new week, or + project on an existing week.") +
                "\nThe type only picks the starting .gitignore.")
            .WithText("Name", first ? "unity-project" : "")
            .WithChoice("Type", [unity, dotnet]);
        dialog.Owner = this;
        dialog.Validate = d =>
            !Regex.IsMatch(d.Text, @"^[A-Za-z0-9._-]+$") ? "Use letters, digits, '.', '_' and '-' only."
            : Directory.Exists(Path.Combine(projects, d.Text)) ? $"{d.Text} already exists."
            : null;
        if (dialog.ShowDialog() != true) return;
        var type = dialog.Selected1 == unity ? ProjectType.Unity : ProjectType.DotNet;
        await RunAsync("Creating project", async () =>
        {
            await _session.Repos.InitRepoAsync(projects, dialog.Text, type);
            SetStatus(first
                ? $"Created {dialog.Text}. Click + to create the first week."
                : $"Added {dialog.Text}. Add it to a week with + or with + project on a week.");
        });
    }

    // ---- safety: unsaved changes and Unity -------------------------------------------------

    /// <summary>
    /// Makes sure the projects have no unsaved changes. If they do, the user chooses to save (commit), put them aside
    /// (stash), throw them away, or cancel. Returns false when the action should not continue.
    /// </summary>
    private async Task<bool> EnsureSavedAsync(IReadOnlyList<RepoState> affected, string action)
    {
        var dirty = affected.Where(r => r.IsDirty).ToList();
        if (dirty.Count == 0)
            return true;
        var dialog = new CleanStateDialog(action, "There are unsaved changes. Deal with them first; they are never carried along.",
            dirty, enteringDemo: false) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Choice is not { } choice)
            return false;
        await _session!.CleanAsync(dirty, choice, dialog.CommitMessageText);
        await _session.RefreshAsync();
        var still = dirty.Select(d => _session.FindRepo(d.Name)).Where(s => s?.IsDirty == true).ToList();
        if (still.Count > 0)
        {
            Message.Show(this, $"Still unsaved changes in {string.Join(", ", still.Select(s => s!.Name))}. Nothing was changed.",
                action, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private bool ConfirmUnityOpen(IEnumerable<RepoState> repos)
    {
        var open = repos.Where(r => r.IsUnityOpen).Select(r => r.Name).ToList();
        if (open.Count == 0)
            return true;
        return Message.Show(this,
            $"Unity seems to have {string.Join(", ", open)} open. Save your scene in Unity first: " +
            "Unity reloads the project after the switch.\n\nContinue?",
            "Unity is open", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private void ShowFailures(SwitchReport report, string title)
    {
        if (report.AllSucceeded) return;
        var text = string.Join("\n", report.Failed.Select(f => $"{f.Repo}: {f.Message}"));
        Message.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ---- log -----------------------------------------------------------------------------

    private void OnLogToggle(object sender, RoutedEventArgs e)
    {
        var show = LogToggle.IsChecked == true;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _log.Count > 0)
            LogList.ScrollIntoView(_log[^1]);
    }

    private void OnLogged(LogEntry entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLogged(entry));
            return;
        }
        if (entry.Kind == LogKind.Query)
            return; // read-only status queries would drown the commands that change something
        var prefix = entry.Kind == LogKind.Command ? $"{entry.Time:HH:mm:ss} [{entry.Repo}] > " : new string(' ', 11);
        var brush = Res(entry.Kind switch
        {
            LogKind.Command => "Text",
            LogKind.Error => "Coral",
            LogKind.Info => "Teal",
            _ => "TextMuted",
        });
        foreach (var line in entry.Text.Replace("\r", "").Split('\n'))
            _log.Add(new LogLine(entry.Kind, prefix + line, brush));
        while (_log.Count > MaxLogLines)
            _log.RemoveAt(0);
        if (LogList.IsVisible)
            LogList.ScrollIntoView(_log[^1]);
    }

    private void OnLogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && LogList.SelectedItems.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, LogList.SelectedItems.Cast<LogLine>().Select(l => l.Text)));
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            Message.Show(this, "Please wait until the current operation has finished.", "DemoDeck");
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher?.Dispose();
        base.OnClosed(e);
    }
}
