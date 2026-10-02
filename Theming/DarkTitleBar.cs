using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameShelf.Theming;

/// <summary>Gives a window the dark title bar of Windows 10 (20H1+) and 11 instead of the default white one.</summary>
internal static class DarkTitleBar
{
    // DwmSetWindowAttribute ids. Colours are COLORREF values (0x00BBGGRR).
    const int UseImmersiveDarkMode = 20, CaptionColor = 35, TextColor = 36;
    const int CaptionDark = 0x00101112, TextLight = 0x00D2E6EF;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Apply(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            int on = 1, caption = CaptionDark, text = TextLight;
            // Unsupported attributes (older Windows) are ignored: the title bar just stays as it was.
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int));
            _ = DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
        };
}
