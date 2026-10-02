using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace GameShelf.Services;

internal enum PadButton { Up, Down, Left, Right, A, B, X, Y, LeftBumper, RightBumper }

/// <summary>
/// Reads an Xbox-style controller (XInput) and raises <see cref="Pressed"/> for each button press. The D-pad and
/// the left stick both give directions, which repeat while held. Nothing is raised while no GameShelf window is
/// in front, so the controller can still be used for a game running behind.
/// </summary>
internal static class Gamepad
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);
    static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan FirstRepeat = TimeSpan.FromMilliseconds(420), NextRepeat = TimeSpan.FromMilliseconds(130);
    const int StickDeadZone = 16000, MaxPads = 4;

    // XINPUT_GAMEPAD_* button bits. The stick sets the matching D-pad bit.
    const ushort DpadUp = 0x1, DpadDown = 0x2, DpadLeft = 0x4, DpadRight = 0x8;
    const ushort Directions = DpadUp | DpadDown | DpadLeft | DpadRight;

    static readonly (ushort Mask, PadButton Button)[] Buttons =
    {
        (DpadUp, PadButton.Up), (DpadDown, PadButton.Down), (DpadLeft, PadButton.Left), (DpadRight, PadButton.Right),
        (0x1000, PadButton.A), (0x2000, PadButton.B), (0x4000, PadButton.X), (0x8000, PadButton.Y),
        (0x0100, PadButton.LeftBumper), (0x0200, PadButton.RightBumper),
    };

    static DispatcherTimer? timer;
    static int pad = -1;
    static ushort previous;
    static DateTime heldSince, lastRepeat, nextSearch;
    static bool unavailable;

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll")]
    static extern int XInputGetState(int userIndex, out XInputState state);

    public static event Action<PadButton>? Pressed;

    /// <summary>Starts listening. Safe to call more than once.</summary>
    public static void Start()
    {
        if (timer is not null) return;
        timer = new DispatcherTimer { Interval = PollInterval };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    static void Poll()
    {
        if (unavailable) return;
        try
        {
            if (!TryRead(out var mask)) return;
            Raise(mask);
        }
        catch (DllNotFoundException)
        {
            unavailable = true; // Windows without XInput: keyboard and mouse only
            timer?.Stop();
        }
    }

    /// <summary>The buttons held now, or false when no controller is connected.</summary>
    static bool TryRead(out ushort mask)
    {
        mask = 0;
        if (pad < 0)
        {
            if (DateTime.UtcNow < nextSearch) return false;
            nextSearch = DateTime.UtcNow + SearchInterval;
            for (int i = 0; i < MaxPads && pad < 0; i++)
                if (XInputGetState(i, out _) == 0) pad = i;
            if (pad < 0) return false;
        }

        if (XInputGetState(pad, out var state) != 0)
        {
            pad = -1; // unplugged
            previous = 0;
            return false;
        }

        mask = state.Buttons;
        if (state.ThumbLX < -StickDeadZone) mask |= DpadLeft;
        if (state.ThumbLX > StickDeadZone) mask |= DpadRight;
        if (state.ThumbLY > StickDeadZone) mask |= DpadUp;
        if (state.ThumbLY < -StickDeadZone) mask |= DpadDown;
        return true;
    }

    static void Raise(ushort mask)
    {
        var now = DateTime.UtcNow;
        var pressed = (ushort)(mask & ~previous);

        // A direction held down repeats, after a first pause.
        var heldDirections = (ushort)(mask & previous & Directions);
        if (heldDirections != 0 && now - heldSince > FirstRepeat && now - lastRepeat > NextRepeat)
        {
            pressed |= heldDirections;
            lastRepeat = now;
        }
        if ((mask & Directions) != (previous & Directions)) heldSince = now;
        previous = mask;

        if (pressed == 0 || !AppIsInFront()) return;
        foreach (var (buttonMask, button) in Buttons)
            if ((pressed & buttonMask) != 0) Pressed?.Invoke(button);
    }

    static bool AppIsInFront() => Application.Current.Windows.OfType<Window>().Any(window => window.IsActive);
}
