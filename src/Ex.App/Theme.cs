using System.Windows;
using System.Windows.Media;
using Ex.Core.Course;

namespace Ex.App;

/// <summary>
/// Switches the surfaces between the two modes. Preparation uses the navy theme from Themes/Dark.xaml;
/// Demo overrides the surfaces with dark teal, so the two can never be confused, even from the back of a classroom.
/// Overrides live in the application's own resources, which win over the merged theme dictionary.
/// </summary>
public static class Theme
{
    private static readonly Dictionary<string, Color> DemoSurfaces = new()
    {
        ["Bg0"] = Color.FromRgb(0x0C, 0x20, 0x24),
        ["Bg1"] = Color.FromRgb(0x11, 0x2A, 0x2F),
        ["Bg2"] = Color.FromRgb(0x16, 0x35, 0x3B),
        ["Bg3"] = Color.FromRgb(0x1E, 0x43, 0x4A),
        ["TextMuted"] = Color.FromRgb(0x82, 0xA6, 0xAB),
        // Teal actions would vanish on teal surfaces: use the palette's warm complement (sand) instead.
        ["Accent"] = Color.FromRgb(0xF3, 0xCA, 0x81),
        ["AccentSoft"] = Color.FromArgb(0x2E, 0xF3, 0xCA, 0x81),
    };

    public static ClientMode Current { get; private set; } = ClientMode.Preparation;

    public static void Apply(ClientMode mode)
    {
        if (mode == Current)
            return;
        Current = mode;
        var resources = Application.Current.Resources;
        foreach (var (key, color) in DemoSurfaces)
        {
            if (mode == ClientMode.Demo)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                resources[key] = brush;
            }
            else
            {
                resources.Remove(key);
            }
        }
        if (mode == ClientMode.Demo)
            resources["Bg0Color"] = DemoSurfaces["Bg0"];
        else
            resources.Remove("Bg0Color");

        foreach (Window window in Application.Current.Windows)
            DarkTitleBar.Apply(window);
    }
}
