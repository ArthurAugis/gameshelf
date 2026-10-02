using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameShelf.Services;

namespace GameShelf.Theming;

/// <summary>Colour helpers used to tint spines and 3D boxes from a game's cover.</summary>
internal static class ColorUtil
{
    // Spine backgrounds must stay dark enough for white text and logos.
    const double MinLuminance = 28, MaxLuminance = 105;

    /// <summary>
    /// Top, middle and bottom colours of a cover. Saturated, bright pixels weigh more, so a
    /// colourful cover is not averaged into grey. Luminance is clamped (see above).
    /// </summary>
    public static Color[] Palette(string coverPath)
    {
        var image = new FormatConvertedBitmap(ImageLoader.Load(coverPath, 24), PixelFormats.Bgra32, null, 0);
        int width = image.PixelWidth, height = image.PixelHeight;
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);

        var palette = new Color[3];
        for (int band = 0; band < palette.Length; band++)
        {
            double r = 0, g = 0, b = 0, totalWeight = 0;
            for (int y = band * height / 3; y < (band + 1) * height / 3; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    double blue = pixels[i], green = pixels[i + 1], red = pixels[i + 2];
                    double max = Math.Max(red, Math.Max(green, blue)), min = Math.Min(red, Math.Min(green, blue));
                    double weight = 0.05 + (max - min) / 255 * max / 255;
                    r += red * weight; g += green * weight; b += blue * weight; totalWeight += weight;
                }
            r /= totalWeight; g /= totalWeight; b /= totalWeight;

            double luminance = 0.3 * r + 0.59 * g + 0.11 * b;
            double factor = luminance > MaxLuminance ? MaxLuminance / luminance
                          : luminance is > 0 and < MinLuminance ? MinLuminance / luminance
                          : 1;
            palette[band] = Color.FromRgb(ToByte(r * factor), ToByte(g * factor), ToByte(b * factor));
        }
        return palette;
    }

    /// <summary>Stable placeholder colour for a game whose cover is not loaded yet (or does not exist).</summary>
    public static Color PlaceholderFor(uint appId)
    {
        var random = new Random((int)appId);
        return FromHsv(random.NextDouble() * 360, 0.45, 0.5);
    }

    /// <summary>Multiplies the brightness: below 1 darkens, above 1 lightens.</summary>
    public static Color Shade(Color color, double factor) =>
        Color.FromRgb(ToByte(color.R * factor), ToByte(color.G * factor), ToByte(color.B * factor));

    static byte ToByte(double value) => (byte)Math.Clamp(value, 0, 255);

    static Color FromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - chroma;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromRgb(ToByte((r + m) * 255), ToByte((g + m) * 255), ToByte((b + m) * 255));
    }
}
