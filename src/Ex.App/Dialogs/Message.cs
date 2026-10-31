using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ex.App.Dialogs;

/// <summary>Themed replacement for <see cref="MessageBox"/> with the same parameters and result.</summary>
public static class Message
{
    public static MessageBoxResult Show(Window owner, string text, string title = "",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var result = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.No,
        };

        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        window.SetResourceReference(Window.BackgroundProperty, "Bg1");
        window.SetResourceReference(Window.ForegroundProperty, "Text");
        window.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        window.FontSize = 13;

        var accent = image switch
        {
            MessageBoxImage.Error => "Coral",
            MessageBoxImage.Warning => "Peach",
            MessageBoxImage.Question => "Sand",
            _ => "Teal",
        };

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var dot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, accent);
        header.Children.Add(dot);
        header.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        root.Children.Add(header);
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(body);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        void Add(string label, MessageBoxResult value, bool primary)
        {
            var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0) };
            if (primary)
                button.SetResourceReference(FrameworkElement.StyleProperty, image == MessageBoxImage.Error && buttons != MessageBoxButton.OK ? "DangerButton" : "PrimaryButton");
            button.IsDefault = defaultResult == value || (defaultResult == MessageBoxResult.None && primary);
            button.IsCancel = value is MessageBoxResult.Cancel || (value == MessageBoxResult.No && buttons == MessageBoxButton.YesNo)
                              || (value == MessageBoxResult.OK && buttons == MessageBoxButton.OK);
            button.Click += (_, _) => { result = value; window.Close(); };
            bar.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.OK:
                Add("OK", MessageBoxResult.OK, true);
                break;
            case MessageBoxButton.OKCancel:
                Add("Cancel", MessageBoxResult.Cancel, false);
                Add("OK", MessageBoxResult.OK, true);
                break;
            case MessageBoxButton.YesNo:
                Add("No", MessageBoxResult.No, false);
                Add("Yes", MessageBoxResult.Yes, true);
                break;
            case MessageBoxButton.YesNoCancel:
                Add("Cancel", MessageBoxResult.Cancel, false);
                Add("No", MessageBoxResult.No, false);
                Add("Yes", MessageBoxResult.Yes, true);
                break;
        }
        root.Children.Add(bar);
        window.Content = root;
        window.ShowDialog();
        return result;
    }
}
