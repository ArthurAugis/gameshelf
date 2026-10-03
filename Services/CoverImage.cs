using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using GameShelf.Theming;

namespace GameShelf.Services;

/// <summary>
/// Makes the 600 x 900 portrait cover of a game added by hand: from any picture (box art of a console is not portrait),
/// or drawn from the program's icon and the game's name when there is no picture. Needs the UI thread.
/// </summary>
internal static class CoverImage
{
    public const int Width = 600, Height = 900;
    const double Portrait = (double)Width / Height;

    /// <summary>
    /// The picture as a cover. A picture that is nearly portrait fills the cover (its edges are cropped); any other
    /// is shown whole, centred over a blurred, darkened copy of itself.
    /// </summary>
    public static byte[] FromPicture(byte[] picture)
    {
        var source = Decode(picture);
        double ratio = (double)source.PixelWidth / source.PixelHeight;
        var root = new Grid { Width = Width, Height = Height, Background = Brushes.Black };

        if (Math.Abs(ratio / Portrait - 1) < 0.2)
        {
            root.Children.Add(new Image { Source = source, Stretch = Stretch.UniformToFill });
        }
        else
        {
            root.Children.Add(new Image { Source = source, Stretch = Stretch.UniformToFill, Effect = new BlurEffect { Radius = 40 } });
            root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) });
            root.Children.Add(new Image { Source = source, Stretch = Stretch.Uniform, Margin = new Thickness(24, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        return Render(root);
    }

    /// <summary>A cover drawn from the program's icon (when it has one) and the game's name, on a colour made from the name.</summary>
    public static byte[] Generate(string title, string? exe)
    {
        var color = ColorUtil.PlaceholderFor(Stable(title));
        var root = new Grid
        {
            Width = Width,
            Height = Height,
            Background = new LinearGradientBrush(ColorUtil.Shade(color, 1.35), ColorUtil.Shade(color, 0.45), 90),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.8, GridUnitType.Star) });

        if (IconOf(exe) is { } icon)
        {
            root.Children.Add(new Image { Source = icon, Width = 220, Height = 220, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24) });
        }
        var name = new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 56,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(36, 12, 36, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.6 },
        };
        Grid.SetRow(name, 1);
        root.Children.Add(name);
        return Render(root);
    }

    /// <summary>The picture of a file, or null when it is not a picture WPF can read.</summary>
    public static byte[]? TryReadPicture(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            Decode(bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The bytes (PNG) of a picture on the clipboard, or null if it holds none.</summary>
    public static byte[]? FromClipboard()
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapImage Decode(byte[] picture)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(picture);
        image.EndInit();
        image.Freeze();
        return image;
    }

    static BitmapSource? IconOf(string? exe)
    {
        if (exe is null || !File.Exists(exe)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            return icon is null ? null : Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    static byte[] Render(FrameworkElement root)
    {
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // The placeholder colour only has to be the same for the same name: string.GetHashCode changes at each run.
    static uint Stable(string title)
    {
        uint hash = 2166136261;
        foreach (var c in title.ToLower(CultureInfo.InvariantCulture)) hash = unchecked((hash ^ c) * 16777619);
        return hash;
    }
}
