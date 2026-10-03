using System.IO;
using System.Runtime.InteropServices;

namespace GameShelf.Services;

/// <summary>What a Windows shortcut (.lnk) starts: its program and the arguments it passes.</summary>
internal sealed record ShortcutTarget(string Target, string Arguments);

internal static class ShortcutFile
{
    /// <summary>
    /// Reads a shortcut through Windows' own scripting object. Null when it cannot be read, or when it starts nothing
    /// a file path can name (a shortcut to a Store app): the shortcut itself then does the job.
    /// </summary>
    public static ShortcutTarget? Resolve(string shortcutPath)
    {
        if (!File.Exists(shortcutPath) || Type.GetTypeFromProgID("WScript.Shell") is not { } shellType) return null;
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic shortcut = link = ((dynamic)shell!).CreateShortcut(shortcutPath);
            string target = shortcut.TargetPath ?? "";
            string arguments = shortcut.Arguments ?? "";
            return target.Length == 0 ? null : new ShortcutTarget(target, arguments);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
