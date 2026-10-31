using System.Windows;
using Ex.Core.Services;

namespace Ex.App.Dialogs;

/// <summary>
/// One merge of a propagation hit files that both sides changed. Per file: keep the later step's version
/// (recommended for Unity scenes and prefabs, which cannot be merged by hand sensibly), take the earlier step's
/// version, or merge by hand.
/// </summary>
public partial class ConflictDialog : Window
{
    public sealed class FileChoice
    {
        public required string File { get; init; }
        public required string KeepLaterText { get; init; }
        public required string KeepLaterNote { get; init; }
        public required string TakeEarlierText { get; init; }
        public bool KeepLater { get; set; } = true;
        public bool TakeEarlier { get; set; }
        public bool ByHand { get; set; }

        public ConflictChoice Choice => TakeEarlier ? ConflictChoice.TakeEarlier : ByHand ? ConflictChoice.ResolvedByHand : ConflictChoice.KeepLater;
    }

    private readonly List<FileChoice> _files;

    public ConflictDialog(IReadOnlyList<string> files, string earlier, string later)
    {
        InitializeComponent();
        HeadingText.Text = $"{Capitalize(later)} also changed {(files.Count == 1 ? "this file" : "these files")}";
        IntroText.Text = $"The saves from {earlier} and the work in {later} touch the same {(files.Count == 1 ? "file" : "files")}. " +
                         "Choose which version to keep.";
        _files = files.Select(f => new FileChoice
        {
            File = f,
            KeepLaterText = $"Keep the version from {later}",
            KeepLaterNote = IsUnityAsset(f) ? "  recommended for scenes and prefabs" : "  the change is marked as synced",
            TakeEarlierText = $"Use the version from {earlier}",
        }).ToList();
        FileList.ItemsSource = _files;
    }

    /// <summary>True: the merge goes on with these choices. False: stop propagating.</summary>
    public bool Continue { get; private set; }

    public IReadOnlyDictionary<string, ConflictChoice> Choices => _files.ToDictionary(f => f.File, f => f.Choice);

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        Continue = true;
        DialogResult = true;
    }

    private void OnAbort(object sender, RoutedEventArgs e)
    {
        Continue = false;
        DialogResult = true;
    }

    private static bool IsUnityAsset(string file) =>
        file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
