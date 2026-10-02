using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameShelf.Services;

namespace GameShelf.Theming;

/// <summary>Look of the shelf: <see cref="Wall"/> is the background, <see cref="Plank"/> the boards the games stand on.</summary>
internal sealed record Skin(Brush Wall, Brush Plank);

/// <summary>
/// The built-in shelf skins (generated, no image assets) and the ones the user imported. An imported skin is a
/// folder in %LOCALAPPDATA%\GameShelf\skins with a <c>plank</c> image and, optionally, a <c>wall</c> image
/// (png, jpg or bmp). Without a wall image the wall is the plank texture, darkened.
/// </summary>
internal static class Skins
{
    const string Folder = "skins";
    const double MaxTileSize = 320;
    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };
    static readonly string[] BuiltInNames = { "Aged wood", "Marble", "Brushed metal", "Slate" };

    static readonly List<string> AllNames = new();
    static readonly Dictionary<string, Skin> Cache = new();

    static Skins() => Reload();

    /// <summary>Built-in skins first, then the imported ones by name.</summary>
    public static IReadOnlyList<string> Names => AllNames;

    public static bool IsImported(string name) => !BuiltInNames.Contains(name);

    public static Skin Get(string name) =>
        Cache.TryGetValue(name, out var skin) ? skin : Cache[name] = Build(name);

    /// <summary>Why this name cannot be used for an imported skin, or null if it can.</summary>
    public static string? Validate(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return Loc.T("Enter a name.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return Loc.T("This name has characters that a folder cannot have.");
        return AllNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? Loc.T("A shelf with this name already exists.") : null;
    }

    /// <summary>Copies the image into the skins folder as the plank texture of a new skin.</summary>
    public static void Import(string name, string imageFile)
    {
        var folder = AppData.PathOf($"{Folder}/{name}");
        Directory.CreateDirectory(folder);
        File.Copy(imageFile, Path.Combine(folder, "plank" + Path.GetExtension(imageFile).ToLowerInvariant()), overwrite: true);
        Cache.Remove(name);
        Reload();
    }

    public static void Delete(string name)
    {
        if (!IsImported(name)) return;
        var folder = AppData.PathOf($"{Folder}/{name}");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Cache.Remove(name);
        Reload();
    }

    static void Reload()
    {
        AllNames.Clear();
        AllNames.AddRange(BuiltInNames);
        var root = AppData.PathOf(Folder);
        if (!Directory.Exists(root)) return;

        var imported = Directory.GetDirectories(root)
            .Where(folder => FindImage(folder, "plank") is not null)
            .Select(folder => Path.GetFileName(folder))
            .Where(name => !BuiltInNames.Contains(name))
            .Order(StringComparer.CurrentCultureIgnoreCase);
        AllNames.AddRange(imported);
    }

    static Skin Build(string name)
    {
        if (!IsImported(name)) return BuildBuiltIn(name);
        try
        {
            return BuildImported(name);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException)
        {
            return BuildBuiltIn(BuiltInNames[0]); // a missing or broken image: show the default wood instead
        }
    }

    // The wall is a darker version of the plank texture.
    static Skin BuildBuiltIn(string name) => name switch
    {
        "Marble" => new(
            Textures.Tile(Textures.Marble, Rgb(60, 60, 68), Rgb(185, 185, 192)),
            Textures.Tile(Textures.Marble, Rgb(130, 130, 138), Rgb(245, 245, 242))),
        "Brushed metal" => new(
            Textures.Tile(Textures.BrushedMetal, Rgb(40, 43, 48), Rgb(95, 100, 108)),
            Textures.Tile(Textures.BrushedMetal, Rgb(120, 126, 134), Rgb(205, 210, 218))),
        "Slate" => new(
            Textures.Tile(Textures.Slate, Rgb(22, 25, 30), Rgb(50, 55, 64)),
            Textures.Tile(Textures.Slate, Rgb(45, 50, 58), Rgb(95, 102, 114))),
        _ => new(
            Textures.Tile(Textures.Wood, Rgb(28, 16, 8), Rgb(70, 42, 22)),
            Textures.Tile(Textures.Wood, Rgb(70, 42, 20), Rgb(150, 100, 55))),
    };

    static Skin BuildImported(string name)
    {
        var folder = AppData.PathOf($"{Folder}/{name}");
        var plankFile = FindImage(folder, "plank") ?? throw new FileNotFoundException("No plank image", folder);
        var plank = ImageLoader.Load(plankFile, 512);
        Brush wall = FindImage(folder, "wall") is { } wallFile
            ? TiledBrush(ImageLoader.Load(wallFile, 512))
            : DarkenedTiledBrush(plank);
        return new Skin(wall, TiledBrush(plank));
    }

    static string? FindImage(string folder, string stem) =>
        ImageExtensions.Select(extension => Path.Combine(folder, stem + extension)).FirstOrDefault(File.Exists);

    /// <summary>The tile is the image at its own size, or smaller if it is big, so the grain looks natural.</summary>
    static Rect TileRect(BitmapSource image)
    {
        double scale = Math.Min(1, MaxTileSize / Math.Max(image.Width, image.Height));
        return new Rect(0, 0, image.Width * scale, image.Height * scale);
    }

    static ImageBrush TiledBrush(BitmapSource image)
    {
        var brush = new ImageBrush(image)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = TileRect(image),
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return brush;
    }

    static DrawingBrush DarkenedTiledBrush(BitmapSource image)
    {
        var tile = TileRect(image);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new ImageDrawing(image, tile));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, new RectangleGeometry(tile)));
        drawing.Freeze();

        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = tile,
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return brush;
    }

    static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
