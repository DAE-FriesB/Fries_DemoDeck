using System.Windows;

namespace Ex.App.Dialogs;

/// <summary>
/// Saving work: stores the changes (a commit) and, on the solution, optionally marks the saved state as the
/// next demo step (a <c>weekNN/step-N</c> branch).
/// </summary>
public partial class SaveDialog : Window
{
    private readonly int _changes;

    /// <param name="changes">Number of changed files.</param>
    /// <param name="nextStep">The step number to offer, or null when steps cannot be marked here.</param>
    public SaveDialog(int changes, int? nextStep)
    {
        InitializeComponent();
        _changes = changes;
        MessagePanel.Visibility = changes > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesText.Text = changes == 1 ? "1 changed file." : $"{changes} changed files.";
        StepPanel.Visibility = nextStep != null ? Visibility.Visible : Visibility.Collapsed;
        StepCheck.Content = $"Mark this as demo step {nextStep}";
        StepCheck.IsChecked = changes == 0;
        Loaded += (_, _) => (changes > 0 ? MessageText : StepTitleBox).Focus();
    }

    public string Message => MessageText.Text.Trim();
    public bool MarkAsStep => StepCheck.IsChecked == true;
    public string? StepTitle => StepTitleBox.Text.Trim() is { Length: > 0 } t ? t : null;

    private void OnStepChanged(object sender, RoutedEventArgs e) => StepTitleBox.IsEnabled = MarkAsStep;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (_changes > 0 && Message.Length == 0)
            error = "Describe what you did (for example: 'Add player movement').";
        else if (_changes == 0 && !MarkAsStep)
            error = "There is nothing to save.";
        if (error != null)
        {
            Dialogs.Message.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
