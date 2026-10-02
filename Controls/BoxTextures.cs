using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameShelf.Models;
using GameShelf.Services;
using GameShelf.Theming;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace GameShelf.Controls;

/// <summary>
/// The printed designs of the 3D box faces (top, bottom, side opposite the spine, back, and a title card for
/// games without a cover). Each one is a WPF element that is rendered to a texture.
/// </summary>
internal static class BoxTextures
{
    // Texture sizes follow the face proportions (see GameBoxView).
    const double CoverWidth = 300, CoverHeight = 450;
    const double EdgeFaceWidth = 400, EdgeFaceHeight = 116; // top and bottom
    const string AppLogoUri = "pack://application:,,,/Assets/logo.png";

    static readonly Color Gold = Color.FromRgb(0xd9, 0xb7, 0x7a);

    /// <summary>Gradient and title, for a game without a portrait cover.</summary>
    public static FrameworkElement TitleCard(Game game, Color[] colors) => new Border
    {
        Width = CoverWidth,
        Height = CoverHeight,
        Background = Vertical(ColorUtil.Shade(colors[0], 1.3), ColorUtil.Shade(colors[2], 0.7)),
        Child = new TextBlock
        {
            Text = DisplayFormat.Shorten(game.Name, 60),
            Foreground = Brushes.White,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        },
    };

    /// <summary>Top of the box: the game's colours with its logo (or title) in the middle.</summary>
    public static FrameworkElement Top(Game game, Color[] colors) => new Border
    {
        Width = EdgeFaceWidth,
        Height = EdgeFaceHeight,
        Background = Horizontal(ColorUtil.Shade(colors[0], 1.1), colors[1], ColorUtil.Shade(colors[2], 1.1)),
        Child = LogoOrTitle(game, maxWidth: 300, maxHeight: 74, titleSize: 36),
    };

    /// <summary>Bottom of the box: darker, with the GameShelf mark.</summary>
    public static FrameworkElement Bottom(Color[] colors)
    {
        var mark = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mark.Children.Add(new Image { Source = ImageLoader.Load(AppLogoUri), Height = 58, Margin = new Thickness(0, 0, 14, 0) });
        mark.Children.Add(new TextBlock
        {
            Text = "GAMESHELF",
            FontFamily = new FontFamily("Georgia"),
            FontSize = 30,
            Foreground = new SolidColorBrush(Gold),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Width = EdgeFaceWidth,
            Height = EdgeFaceHeight,
            Background = Horizontal(
                ColorUtil.Shade(colors[2], 0.55), ColorUtil.Shade(colors[1], 0.4), ColorUtil.Shade(colors[2], 0.55)),
            Child = mark,
        };
    }

    /// <summary>Side opposite the spine: same bands as the spine, the logo of each launcher in the middle, the GameShelf mark on top.</summary>
    public static FrameworkElement Side(Game game, Color[] colors)
    {
        const double bandHeight = 28;
        var band = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(bandHeight) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(bandHeight) });

        grid.Children.Add(new Border
        {
            Background = band,
            Child = new Image { Source = ImageLoader.Load(AppLogoUri, 64), Width = 20, Height = 20 },
        });

        // The logo of each launcher the game is owned on, one above the other (the title is already on the spine).
        var logos = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.85,
        };
        foreach (var launcher in game.OwnedOn)
        {
            var logo = PlatformLogos.Create(launcher, 28);
            logo.Margin = new Thickness(0, logos.Children.Count == 0 ? 0 : 16, 0, 0);
            logos.Children.Add(logo);
        }
        Grid.SetRow(logos, 1);
        grid.Children.Add(logos);

        var bottom = new Border { Background = band };
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);

        return new Border
        {
            Width = SpineView.SpineWidth,
            Height = SpineView.SpineHeight,
            Background = Vertical(colors[0], colors[1], colors[2]),
            Child = grid,
        };
    }

    /// <summary>
    /// Back cover: logo, a banner of the game's artwork, genres and description, publisher line and a barcode.
    /// <paramref name="details"/> and <paramref name="heroPath"/> may be null and are added when they arrive.
    /// </summary>
    public static FrameworkElement Back(Game game, Color[] colors, GameDetails? details, string? heroPath)
    {
        var grid = new Grid { Margin = new Thickness(22) };
        for (int i = 0; i < 4; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var heading = LogoOrTitle(game, maxWidth: 250, maxHeight: 64, titleSize: 26);
        heading.Margin = new Thickness(0, 0, 0, 16);
        grid.Children.Add(heading);

        if (heroPath is not null)
        {
            var banner = new Border
            {
                Height = 112,
                CornerRadius = new CornerRadius(6),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = new ImageBrush(ImageLoader.Load(heroPath, 700)) { Stretch = Stretch.UniformToFill },
            };
            Grid.SetRow(banner, 1);
            grid.Children.Add(banner);
        }

        var text = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (details is { Genres.Count: > 0 })
            text.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", details.Genres.Take(3)).ToUpperInvariant(),
                Foreground = new SolidColorBrush(Gold),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
            });
        if (details?.Summary(230) is { } summary)
            text.Children.Add(new TextBlock
            {
                Text = summary,
                Foreground = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)),
                FontSize = 11.5,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap,
            });
        Grid.SetRow(text, 2);
        grid.Children.Add(text);

        var footer = CreateFooter(game, details);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        return new Border
        {
            Width = CoverWidth,
            Height = CoverHeight,
            Background = Vertical(ColorUtil.Shade(colors[0], 0.9), ColorUtil.Shade(colors[1], 0.7), ColorUtil.Shade(colors[2], 0.5)),
            Child = grid,
        };
    }

    /// <summary>
    /// A face of the box (the cover, or the title card) with the logo of each launcher the game is owned on, in a dark
    /// pill in its bottom left corner.
    /// </summary>
    public static FrameworkElement WithPlatforms(FrameworkElement face, Game game)
    {
        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = PlatformLogos.Row(game.OwnedOn, 20, 8),
        };
        return new Grid { Width = CoverWidth, Height = CoverHeight, Children = { face, pill } };
    }

    /// <summary>Renders an off-screen element to a bitmap so it can be used as a 3D texture.</summary>
    public static ImageBrush Rasterize(FrameworkElement element, double scale)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)(element.Width * scale), (int)(element.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return new ImageBrush(bitmap);
    }

    static Grid CreateFooter(Game game, GameDetails? details)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        info.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 1, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = "PC", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.Black },
        });
        var creditParts = new[] { details?.Developer, details?.ReleaseDate }.Where(s => !string.IsNullOrWhiteSpace(s));
        var credit = string.Join("  ·  ", creditParts);
        if (credit.Length > 0)
            info.Children.Add(new TextBlock
            {
                Text = credit,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                FontSize = 10,
                Margin = new Thickness(0, 6, 0, 0),
                MaxWidth = 150,
                TextWrapping = TextWrapping.Wrap,
            });

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(info);
        footer.Children.Add(Barcode(game.AppId));
        return footer;
    }

    /// <summary>A decorative barcode, different for each game.</summary>
    static Border Barcode(uint seed)
    {
        var random = new Random((int)seed);
        var bars = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 30; i++)
            bars.Children.Add(new Rectangle
            {
                Width = random.Next(1, 4),
                Height = 30,
                Fill = Brushes.Black,
                Margin = new Thickness(0, 0, random.Next(1, 3), 0),
            });
        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 5, 4, 5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = bars,
        };
    }

    static FrameworkElement LogoOrTitle(Game game, double maxWidth, double maxHeight, double titleSize)
    {
        FrameworkElement element = game.Logo is { } logoPath
            ? new Image
            {
                Source = ImageLoader.Load(logoPath, 500),
                Stretch = Stretch.Uniform,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
            }
            : new TextBlock
            {
                Text = DisplayFormat.Shorten(game.Name, 40),
                Foreground = Brushes.White,
                FontSize = titleSize,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = maxWidth,
            };
        RenderOptions.SetBitmapScalingMode(element, BitmapScalingMode.HighQuality);
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    static LinearGradientBrush Vertical(params Color[] colors) => Gradient(new Point(0, 0), new Point(0, 1), colors);

    static LinearGradientBrush Horizontal(params Color[] colors) => Gradient(new Point(0, 0), new Point(1, 0), colors);

    static LinearGradientBrush Gradient(Point start, Point end, Color[] colors)
    {
        var stops = new GradientStopCollection();
        for (int i = 0; i < colors.Length; i++) stops.Add(new GradientStop(colors[i], i / (double)(colors.Length - 1)));
        return new LinearGradientBrush(stops, start, end);
    }
}
