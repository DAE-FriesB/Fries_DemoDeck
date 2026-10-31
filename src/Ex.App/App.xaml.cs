using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Ex.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Every window gets the dark theme's font, text colour and a title bar that matches the page.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        base.OnStartup(e);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window))
            DarkTitleBar.Apply(window);
    }
}

/// <summary>Asks Windows (10 20H1+ / 11) to draw the title bar in the theme's colours.</summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var on = 1;
        DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int));
        if (Application.Current.TryFindResource("Bg0Color") is Color bg)
        {
            var caption = ToColorRef(bg);
            DwmSetWindowAttribute(hwnd, CaptionColor, ref caption, sizeof(int));
        }
        var text = ToColorRef(Color.FromRgb(0xEE, 0xF0, 0xF7));
        DwmSetWindowAttribute(hwnd, TextColor, ref text, sizeof(int));
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
