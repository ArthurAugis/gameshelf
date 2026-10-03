using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GameShelf.Models;
using GameShelf.Services;
using GameShelf.Theming;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace GameShelf.Controls;

/// <summary>
/// One game seen from the side, like the spine of a boxed game: a colour gradient taken from the cover,
/// a mini cover on top, and the logo (or the title) rotated along the spine. Grows slightly on hover.
/// </summary>
internal sealed class SpineView : Border
{
    public const double SpineWidth = 46, SpineHeight = 240;

    /// <summary>Data format of a spine being dragged: the game's app id (a <see cref="uint"/>).</summary>
    public const string DragFormat = "GameShelf.AppId";

    // Vertical layout: mini cover area, logo area (the rest), installed-marker band.
    const double TopHeight = 58, BottomHeight = 24;
    const double LogoLength = SpineHeight - TopHeight - BottomHeight - 14;

    // Tooltip offset from the mouse pointer: below and to the right of it.
    const double TooltipGapX = 14, TooltipGapY = 22, TooltipMaxWidth = 320;

    static readonly Brush FocusBrush = new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a));

    readonly ScaleTransform hoverScale = new(1, 1);
    readonly DispatcherTimer dwellTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly ToolTip tooltip;
    readonly TextBlock tooltipText;
    Color[]? palette;
    Point? dragStart;

    public SpineView(Game game, Color placeholderColor)
    {
        Game = game;
        Color = placeholderColor;

        Width = SpineWidth;
        Height = SpineHeight;
        Margin = new Thickness(1, 0, 1, 0);
        ClipToBounds = true;
        CornerRadius = new CornerRadius(2, 2, 0, 0);
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 1); // grow upwards from the shelf
        RenderTransform = hoverScale;

        // Long titles wrap onto several lines instead of stretching across the screen.
        tooltipText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = TooltipMaxWidth };
        tooltip = new ToolTip { Content = tooltipText, Placement = PlacementMode.Relative };
        ToolTip = tooltip;
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetBetweenShowDelay(this, 0);

        Build();

        dwellTimer.Tick += (_, _) => { dwellTimer.Stop(); Dwelled?.Invoke(this); };

        MouseEnter += (_, _) => { Panel.SetZIndex(this, 10); Grow(1.10, 1.05); FollowCursor(); dwellTimer.Start(); };
        MouseMove += OnMouseMoved;
        MouseLeave += (_, _) => { Panel.SetZIndex(this, 0); Grow(1, 1); dwellTimer.Stop(); dragStart = null; };
        MouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
        MouseLeftButtonUp += (_, _) => { dragStart = null; Clicked?.Invoke(this); };
    }

    public Game Game { get; }

    /// <summary>Top, middle and bottom colours of the cover; null until the cover has been read.</summary>
    public Color[]? Palette => palette;

    /// <summary>Main colour of the spine: a placeholder at first, the cover's middle colour once known.</summary>
    public Color Color { get; private set; }

    public event Action<SpineView>? Clicked;

    /// <summary>The mouse stayed on the spine for a moment: the user is probably about to open it.</summary>
    public event Action<SpineView>? Dwelled;

    /// <summary>Tints the spine with the top, middle and bottom colours of the cover.</summary>
    public void SetPalette(Color[] colors)
    {
        palette = colors;
        Color = colors[1];
        Build();
    }

    /// <summary>Marks the spine as the one chosen with the keyboard or the controller: gold edge, raised like on hover.</summary>
    public void SetFocused(bool focused)
    {
        BorderBrush = focused ? FocusBrush : null;
        BorderThickness = new Thickness(focused ? 2 : 0);
        Panel.SetZIndex(this, focused ? 10 : 0);
        Grow(focused ? 1.10 : 1, focused ? 1.05 : 1);
    }

    /// <summary>Redraws the spine, for when the game's cover or logo was filled in.</summary>
    public void Refresh() => Build();

    void Build()
    {
        var colors = palette ?? new[] { ColorUtil.Shade(Color, 1.15), Color, ColorUtil.Shade(Color, 0.7) };
        Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(colors[0], 0), new(colors[1], 0.5), new(colors[2], 1),
        }, new Point(0, 0), new Point(0, 1));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TopHeight) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BottomHeight) });

        if (Game.Cover is { } coverPath) grid.Children.Add(CreateMiniCover(coverPath));

        var note = GameNotes.Get(Game.KeyId);
        tooltipText.Text = TooltipOf(note);
        if (StatusColor(note.Status) is { } statusColor) grid.Children.Add(CreateStatusDot(statusColor));

        var label = CreateLabel();
        Grid.SetRow(label, 1);
        grid.Children.Add(label);

        var bottom = CreateBottomBand();
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);

        var edgeShading = CreateEdgeShading();
        Grid.SetRowSpan(edgeShading, 3);
        grid.Children.Add(edgeShading);

        Child = grid;
    }

    /// <summary>The title, and under it the user's status and rating when there are some.</summary>
    string TooltipOf(GameNote note)
    {
        var personal = new List<string>();
        if (note.Status != PlayStatus.None) personal.Add(Loc.T(note.Status.ToString()));
        if (note.Rating > 0) personal.Add($"{note.Rating}/{GameNotes.MaxRating}");
        var platforms = string.Join(" + ", Game.OwnedOn.Select(Game.LabelOf));
        return personal.Count == 0 ? $"{Game.Name}\n{platforms}" : $"{Game.Name}\n{platforms}\n{string.Join("  ·  ", personal)}";
    }

    /// <summary>Colour of the status dot, or null when the game has no status.</summary>
    public static Color? StatusColor(PlayStatus status) => status switch
    {
        PlayStatus.Playing => Color.FromRgb(0x4a, 0xa3, 0xff),
        PlayStatus.Finished => Color.FromRgb(0xd9, 0xb7, 0x7a),
        PlayStatus.Dropped => Color.FromRgb(0xc2, 0x55, 0x4a),
        _ => null,
    };

    /// <summary>Small dot in the top corner: the user's status for this game.</summary>
    static Ellipse CreateStatusDot(Color color) => new()
    {
        Width = 11,
        Height = 11,
        Fill = new SolidColorBrush(color),
        Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
        StrokeThickness = 1.2,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 3, 3, 0),
    };

    static Border CreateMiniCover(string coverPath)
    {
        var miniCover = new Border
        {
            Width = 32,
            Height = 46,
            CornerRadius = new CornerRadius(2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Background = new ImageBrush(ImageLoader.Load(coverPath, 96)) { Stretch = Stretch.UniformToFill },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0),
        };
        RenderOptions.SetBitmapScalingMode(miniCover, BitmapScalingMode.HighQuality);
        return miniCover;
    }

    /// <summary>The game logo, or its title when there is no logo, rotated to read along the spine.</summary>
    FrameworkElement CreateLabel()
    {
        FrameworkElement label = Game.Logo is { } logoPath
            ? new Image
            {
                Source = ImageLoader.Load(logoPath, 300),
                Stretch = Stretch.Uniform,
                MaxWidth = LogoLength,
                MaxHeight = SpineWidth - 12,
            }
            : new TextBlock
            {
                Text = Game.Name,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = LogoLength,
            };
        RenderOptions.SetBitmapScalingMode(label, BitmapScalingMode.HighQuality);
        label.LayoutTransform = new RotateTransform(90);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    Border CreateBottomBand()
    {
        var band = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        // The logo of each launcher the game is owned on, and the installed marker.
        var content = PlatformLogos.Row(Game.OwnedOn, 11, 3, Game.Console);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        if (Game.Installed) content.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brushes.LimeGreen, Margin = new Thickness(5, 0, 0, 0) });
        band.Child = content;
        return band;
    }

    /// <summary>Dark edges and a light centre line give the flat spine a rounded look.</summary>
    static Border CreateEdgeShading() => new()
    {
        IsHitTestVisible = false,
        Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(110, 0, 0, 0), 0),
            new(Color.FromArgb(0, 0, 0, 0), 0.25),
            new(Color.FromArgb(40, 255, 255, 255), 0.45),
            new(Color.FromArgb(0, 0, 0, 0), 0.7),
            new(Color.FromArgb(120, 0, 0, 0), 1),
        }, new Point(0, 0), new Point(1, 0)),
    };

    /// <summary>
    /// Keeps the tooltip next to the mouse pointer. WPF places a tooltip once, when it opens, so its offset is
    /// updated on every move. With <see cref="PlacementMode.Relative"/> the offset is measured from this spine
    /// (WPF sets it as the placement target itself when the tooltip opens).
    /// </summary>
    void FollowCursor()
    {
        var position = Mouse.GetPosition(this);
        tooltip.HorizontalOffset = position.X + TooltipGapX;
        tooltip.VerticalOffset = position.Y + TooltipGapY;
    }

    /// <summary>Moving the tooltip along, and starting a drag (to a collection) once the pointer moved far enough.</summary>
    void OnMouseMoved(object sender, MouseEventArgs e)
    {
        FollowCursor();
        if (e.LeftButton != MouseButtonState.Pressed || dragStart is not { } start) return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        dragStart = null;
        tooltip.IsOpen = false;
        dwellTimer.Stop();
        DragDrop.DoDragDrop(this, new DataObject(DragFormat, Game.KeyId), DragDropEffects.Link);

        // MouseLeave is not raised while dragging: put the spine back down by hand.
        Panel.SetZIndex(this, 0);
        Grow(1, 1);
    }

    void Grow(double scaleX, double scaleY)
    {
        var duration = TimeSpan.FromMilliseconds(120);
        hoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scaleX, duration));
        hoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scaleY, duration));
    }
}
