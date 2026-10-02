using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameShelf.Steam;

/// <summary>
/// Keeps Steam's windows out of the way while GameShelf drives Steam. Steam's install manager brings up its own
/// install window and its main window; any Steam window that was not on screen before is put back (hidden, or
/// minimized if it was minimized). A system event reacts the moment a window is shown, and a fast poll
/// catches the rest, so the window never gets the time to be seen.
/// </summary>
internal static class SteamWindows
{
    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    const int Hide = 0, ShowMinimizedNoActivate = 7;
    const uint EventObjectShow = 0x8002, OutOfContext = 0;
    const int WindowObjectId = 0; // OBJID_WINDOW: the event is about a window, not one of its parts
    const int PollMilliseconds = 10, RefreshProcessesEvery = 10; // polls
    static readonly TimeSpan KeepGuardingAfterAction = TimeSpan.FromSeconds(3); // the windows can appear a bit late

    // Steam's interface is drawn by its embedded browser, so its windows belong to steamwebhelper too.
    static readonly string[] ProcessNames = { "steam", "steamwebhelper" };

    readonly record struct WindowState(bool Visible, bool Minimized);

    // Shared by the poll and the event callback. Kept in fields: the callback delegate must stay alive.
    static readonly WinEventProc ShowCallback = OnWindowShown;
    static Dictionary<IntPtr, WindowState> snapshot = new();
    static volatile HashSet<uint> steamProcessIds = new();
    static int activeSessions;

    /// <summary>
    /// Runs <paramref name="action"/> while watching Steam's windows, and keeps watching a few seconds after it
    /// ends. Call it from the UI thread: the event hook needs a message loop.
    /// </summary>
    public static async Task<T> KeepHiddenAsync<T>(Func<Task<T>> action)
    {
        if (Interlocked.Increment(ref activeSessions) == 1) snapshot = TakeSnapshot();
        var uiContext = SynchronizationContext.Current;
        var hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, ShowCallback, 0, 0, OutOfContext);

        var stop = new CancellationTokenSource();
        var guard = Task.Run(async () =>
        {
            for (int poll = 0; !stop.IsCancellationRequested; poll++)
            {
                if (poll % RefreshProcessesEvery == 0) steamProcessIds = FindSteamProcessIds();
                ForEachSteamWindow(HideIfNewlyShown);
                await Task.Delay(PollMilliseconds);
            }
        });
        try
        {
            return await action();
        }
        finally
        {
            stop.CancelAfter(KeepGuardingAfterAction);
            _ = guard.ContinueWith(_ =>
            {
                stop.Dispose();
                Interlocked.Decrement(ref activeSessions);
                // A hook must be removed from the thread that installed it.
                if (uiContext is not null) uiContext.Post(__ => UnhookWinEvent(hook), null);
                else UnhookWinEvent(hook);
            });
        }
    }

    static void OnWindowShown(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != WindowObjectId || window == IntPtr.Zero) return;
        _ = GetWindowThreadProcessId(window, out var processId);
        if (steamProcessIds.Contains(processId))
            HideIfNewlyShown(window, new WindowState(IsWindowVisible(window), IsIconic(window)));
    }

    static void HideIfNewlyShown(IntPtr window, WindowState now)
    {
        var previous = snapshot.GetValueOrDefault(window); // a window created since: it was not on screen
        bool shownNow = now.Visible && !now.Minimized;
        bool wasShown = previous.Visible && !previous.Minimized;
        if (shownNow && !wasShown) ShowWindow(window, previous.Visible ? ShowMinimizedNoActivate : Hide);
    }

    static Dictionary<IntPtr, WindowState> TakeSnapshot()
    {
        steamProcessIds = FindSteamProcessIds();
        var windows = new Dictionary<IntPtr, WindowState>();
        ForEachSteamWindow((window, state) => windows[window] = state);
        return windows;
    }

    static HashSet<uint> FindSteamProcessIds() =>
        ProcessNames.SelectMany(Process.GetProcessesByName).Select(process => (uint)process.Id).ToHashSet();

    static void ForEachSteamWindow(Action<IntPtr, WindowState> visit)
    {
        var processIds = steamProcessIds;
        if (processIds.Count == 0) return;

        EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processIds.Contains(processId))
                visit(window, new WindowState(IsWindowVisible(window), IsIconic(window)));
            return true;
        }, IntPtr.Zero);
    }
}
