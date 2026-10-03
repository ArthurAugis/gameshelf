using System.Windows.Media.Imaging;

namespace GameShelf.Services;

internal static class ImageLoader
{
    /// <summary>
    /// Loads an image file fully into memory (no file lock) and freezes it.
    /// <paramref name="decodeWidth"/> decodes at a reduced size to save memory; 0 keeps the original size.
    /// </summary>
    public static BitmapImage Load(string path, int decodeWidth = 0)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // a cover that was replaced under the same name must show
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
