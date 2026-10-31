using System.Windows;
using System.Windows.Controls;

namespace Ex.App.Dialogs;

/// <summary>A small generic dialog: an optional text field and up to two drop-downs.</summary>
public partial class PromptDialog : Window
{
    public PromptDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = Visibility.Collapsed;
        Choice1Panel.Visibility = Visibility.Collapsed;
        Choice2Panel.Visibility = Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            if (TextPanel.Visibility == Visibility.Visible)
            {
                InputText.Focus();
                InputText.SelectAll();
            }
        };
    }

    public PromptDialog WithText(string label, string value = "")
    {
        TextPanel.Visibility = Visibility.Visible;
        TextLabel.Text = label;
        InputText.Text = value;
        return this;
    }

    public PromptDialog WithChoice(string label, IEnumerable<string> items, string? selected = null) =>
        Choice1Panel.Visibility == Visibility.Collapsed
            ? Fill(Choice1Panel, Choice1Label, Choice1, label, items, selected)
            : Fill(Choice2Panel, Choice2Label, Choice2, label, items, selected);

    public string Text => InputText.Text.Trim();
    public string? Selected1 => Choice1.SelectedItem as string;
    public string? Selected2 => Choice2.SelectedItem as string;

    /// <summary>Optional validation; return an error message to keep the dialog open.</summary>
    public Func<PromptDialog, string?>? Validate { get; set; }

    private PromptDialog Fill(FrameworkElement panel, TextBlock labelBlock, ComboBox combo,
        string label, IEnumerable<string> items, string? selected)
    {
        panel.Visibility = Visibility.Visible;
        labelBlock.Text = label;
        var list = items.ToList();
        combo.ItemsSource = list;
        combo.SelectedItem = selected != null && list.Contains(selected) ? selected : list.FirstOrDefault();
        return this;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TextPanel.Visibility == Visibility.Visible && Text.Length == 0)
        {
            Message.Show(this, $"{TextLabel.Text} is required.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Validate?.Invoke(this) is { } error)
        {
            Message.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
