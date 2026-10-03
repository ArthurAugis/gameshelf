using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GameShelf.Controls;
using GameShelf.Services;

namespace GameShelf.Views;

/// <summary>The main window: keyboard and controller navigation, and fullscreen.</summary>
internal sealed partial class MainWindow : Window
{
    // ---- Keyboard and controller navigation ----

    double ShelfScale => fullscreen ? FullscreenScale : 1;

    /// <summary>Handles an arrow key or Enter. False if the key is not for the shelf.</summary>
    bool TryNavigate(Key key)
    {
        switch (key)
        {
            case Key.Left: MoveFocus(-1, 0); return true;
            case Key.Right: MoveFocus(1, 0); return true;
            case Key.Up: MoveFocus(0, -1); return true;
            case Key.Down: MoveFocus(0, 1); return true;
            case Key.Enter when focused is not null:
                OpenDetails(focused);
                return true;
            default: return false;
        }
    }

    void OnGamepadPressed(PadButton button)
    {
        if (!IsActive || detail is not null || DialogHost.IsOpen || Splash.Visibility == Visibility.Visible) return;
        switch (button)
        {
            case PadButton.Left: MoveFocus(-1, 0); break;
            case PadButton.Right: MoveFocus(1, 0); break;
            case PadButton.Up: MoveFocus(0, -1); break;
            case PadButton.Down: MoveFocus(0, 1); break;
            case PadButton.A when focused is not null: OpenDetails(focused); break;
            case PadButton.Y: ToggleFullscreen(); break;
            case PadButton.LeftBumper: CycleCollection(-1); break;
            case PadButton.RightBumper: CycleCollection(1); break;
        }
    }

    /// <summary>
    /// Moves the chosen spine one place along a row or to the row above or below. The first press only
    /// chooses the first game.
    /// </summary>
    void MoveFocus(int columns, int rows)
    {
        if (layoutRows.Count == 0) return;
        if (focused is null)
        {
            SetFocus(layoutRows[0][0]);
            return;
        }

        int row = layoutRows.FindIndex(r => r.Contains(focused));
        if (row < 0)
        {
            SetFocus(layoutRows[0][0]);
            return;
        }
        int column = layoutRows[row].IndexOf(focused);

        row = Math.Clamp(row + rows, 0, layoutRows.Count - 1);
        column = Math.Clamp(column + columns, 0, layoutRows[row].Count - 1);
        SetFocus(layoutRows[row][column]);
    }

    void SetFocus(SpineView? spine, bool scrollIntoView = true)
    {
        focused?.SetFocused(false);
        focused = spine;
        if (spine is null)
        {
            FocusBar.Visibility = Visibility.Collapsed;
            return;
        }

        spine.SetFocused(true);
        if (scrollIntoView) spine.BringIntoView(new Rect(-30, -40, SpineView.SpineWidth + 60, SpineView.SpineHeight + 120));
        FocusTitle.Text = spine.Game.Name;
        FocusHint.Text = Loc.T("Enter / A: open");
        FocusBar.Visibility = Visibility.Visible;
    }

    /// <summary>After the shelf was rebuilt the chosen game has new spines (and maybe moved): find it again.</summary>
    void RestoreFocus()
    {
        var previous = focused;
        focused = null;
        var match = previous is null ? null : layoutRows.SelectMany(r => r).FirstOrDefault(s => s.Game.AppId == previous.Game.AppId);
        SetFocus(match, scrollIntoView: false);
    }

    /// <summary>The mouse takes over again: drop the keyboard choice.</summary>
    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (focused is not null && (position - lastMousePosition).Length > 6) SetFocus(null);
        lastMousePosition = position;
    }

    /// <summary>Selects the next or previous collection (All games, then each collection).</summary>
    void CycleCollection(int step)
    {
        var choices = new List<string?> { null };
        choices.AddRange(GameCollections.Names);
        int index = Math.Max(0, choices.IndexOf(filter.Collection));
        filter.Collection = choices[(index + step + choices.Count) % choices.Count];
        BuildCollectionBar();
        RebuildShelf();
    }

    // ---- Fullscreen ----

    /// <summary>Fullscreen shows bigger spines, to read from a sofa. F11, Y on the controller, or --bigpicture.</summary>
    void ToggleFullscreen()
    {
        fullscreen = !fullscreen;
        if (fullscreen)
        {
            windowStateBeforeFullscreen = WindowState;
            WindowState = WindowState.Normal; // a maximized window must be restored before its frame can be removed
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = windowStateBeforeFullscreen;
        }
        ShelfRows.LayoutTransform = new ScaleTransform(ShelfScale, ShelfScale);
        RebuildShelf();
    }

    Border CreatePlank() => new()
    {
        Height = 26,
        Margin = new Thickness(-14, 0, -14, 34),
        Background = skin.Plank,
        Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 5, Opacity = 0.65 },
        Child = new Border // light edge on top of the plank
        {
            Height = 5,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush(Color.FromArgb(90, 255, 255, 255), Colors.Transparent, 90),
        },
    };
}
