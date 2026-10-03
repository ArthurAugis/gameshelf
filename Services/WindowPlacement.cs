using System.Text.Json;
using System.Windows;

namespace GameShelf.Services;

/// <summary>Remembers where the main window was, and how big, between runs (<c>window.json</c>).</summary>
internal static class WindowPlacement
{
    const string FileName = "window.json";
    const double MinWidth = 820, MinHeight = 520; // the main window's own minimum (MainWindow.xaml)

    sealed record Saved(double Left, double Top, double Width, double Height, bool Maximized);

    /// <summary>Puts the window back as it was. A saved place that is off every screen (a monitor was unplugged) is ignored.</summary>
    public static void Restore(Window window)
    {
        Saved? saved;
        try
        {
            saved = AppData.ReadText(FileName) is { } json ? JsonSerializer.Deserialize<Saved>(json) : null;
        }
        catch (JsonException)
        {
            return;
        }
        if (saved is null || !IsUsable(saved)) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = saved.Left;
        window.Top = saved.Top;
        window.Width = saved.Width;
        window.Height = saved.Height;
        if (saved.Maximized) window.WindowState = WindowState.Maximized;
    }

    /// <summary>Saves the window's normal bounds (not the maximized ones) and whether it was maximized.</summary>
    public static void Save(Window window, bool maximized)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        if (bounds.IsEmpty) return;
        AppData.WriteText(FileName, JsonSerializer.Serialize(new Saved(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized)));
    }

    static bool IsUsable(Saved saved)
    {
        if (saved.Width < MinWidth || saved.Height < MinHeight) return false;
        // Enough of the title bar must be on a screen for the window to be grabbed.
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleBar = new Rect(saved.Left, saved.Top, saved.Width, 40);
        titleBar.Intersect(screens);
        return !titleBar.IsEmpty && titleBar.Width >= 100;
    }
}
