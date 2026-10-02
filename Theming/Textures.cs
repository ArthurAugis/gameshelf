using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameShelf.Theming;

/// <summary>
/// Procedural, seamlessly tiling textures (wood, marble, brushed metal, slate), so the app needs no image assets.
/// Each pattern maps a position (u, v in [0, 1)) to a value in [0, 1] that is then mapped between two colours.
/// </summary>
internal static class Textures
{
    const int TileSize = 256;

    public static double Wood(double u, double v)
    {
        double grain = Fbm(u, v, 3, 28);
        return 0.55 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (8 * v + 3 * grain))) + 0.45 * grain;
    }

    public static double Marble(double u, double v)
    {
        double turbulence = Fbm(u, v, 3, 3);
        double vein = Math.Pow(Math.Abs(Math.Sin(2 * Math.PI * (u + v + 1.2 * turbulence))), 0.18);
        return 0.85 * vein + 0.15 * Fbm(u, v, 8, 8);
    }

    public static double BrushedMetal(double u, double v) =>
        0.65 * ValueNoise(u * 2, v * 220, 2, 220) + 0.35 * Fbm(u, v, 2, 2);

    public static double Slate(double u, double v) => Fbm(u, v, 6, 6);

    /// <summary>A tiled brush: <paramref name="pattern"/> mapped from <paramref name="dark"/> to <paramref name="light"/>.</summary>
    public static ImageBrush Tile(Func<double, double, double> pattern, Color dark, Color light)
    {
        var pixels = new byte[TileSize * TileSize * 4];
        for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                double t = Math.Clamp(pattern(x / (double)TileSize, y / (double)TileSize), 0, 1);
                int i = (y * TileSize + x) * 4;
                pixels[i] = (byte)(dark.B + (light.B - dark.B) * t);
                pixels[i + 1] = (byte)(dark.G + (light.G - dark.G) * t);
                pixels[i + 2] = (byte)(dark.R + (light.R - dark.R) * t);
                pixels[i + 3] = 255;
            }

        var bitmap = BitmapSource.Create(TileSize, TileSize, 96, 96, PixelFormats.Bgra32, null, pixels, TileSize * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, TileSize, TileSize),
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return brush;
    }

    // Value noise whose lattice wraps at (periodX, periodY), so tiles have no visible seams.
    static double LatticeValue(int x, int y, int periodX, int periodY)
    {
        x = (x % periodX + periodX) % periodX;
        y = (y % periodY + periodY) % periodY;
        uint hash = unchecked((uint)(x * 374761393 + y * 668265263));
        hash = unchecked((hash ^ (hash >> 13)) * 1274126177u);
        return (hash ^ (hash >> 16)) / (double)uint.MaxValue;
    }

    static double ValueNoise(double x, double y, int periodX, int periodY)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        fx = fx * fx * (3 - 2 * fx); // smoothstep
        fy = fy * fy * (3 - 2 * fy);
        double a = LatticeValue(ix, iy, periodX, periodY);
        double b = LatticeValue(ix + 1, iy, periodX, periodY);
        double c = LatticeValue(ix, iy + 1, periodX, periodY);
        double d = LatticeValue(ix + 1, iy + 1, periodX, periodY);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    /// <summary>Four octaves of tileable value noise.</summary>
    static double Fbm(double u, double v, int periodX, int periodY)
    {
        double sum = 0, amplitude = 0.5, total = 0;
        for (int octave = 0; octave < 4; octave++, amplitude /= 2)
        {
            sum += amplitude * ValueNoise(u * periodX * (1 << octave), v * periodY * (1 << octave),
                                          periodX << octave, periodY << octave);
            total += amplitude;
        }
        return sum / total;
    }
}
