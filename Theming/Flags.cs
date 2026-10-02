using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GameShelf.Theming;

/// <summary>
/// Small flags drawn with shapes, one per language. Windows shows the flag emojis as two letters, so they are drawn
/// here instead of using text or picture files. The flag stands for the language, not for a country.
/// </summary>
internal static class Flags
{
    public static FrameworkElement Create(string languageCode, double height = 16)
    {
        var canvas = new Canvas { Width = 60, Height = 30, ClipToBounds = true };
        switch (languageCode)
        {
            case "fr":
                Fill(canvas, 0, 0, 20, 30, "#0055A4");
                Fill(canvas, 20, 0, 20, 30, "#FFFFFF");
                Fill(canvas, 40, 0, 20, 30, "#EF4135");
                break;
            case "de":
                Fill(canvas, 0, 0, 60, 10, "#000000");
                Fill(canvas, 0, 10, 60, 10, "#DD0000");
                Fill(canvas, 0, 20, 60, 10, "#FFCE00");
                break;
            case "es":
                Fill(canvas, 0, 0, 60, 30, "#AA151B");
                Fill(canvas, 0, 7.5, 60, 15, "#F1BF00");
                break;
            default: // the Union Jack, for English
                Fill(canvas, 0, 0, 60, 30, "#012169");
                Stripe(canvas, 0, 0, 60, 30, 6, "#FFFFFF");
                Stripe(canvas, 60, 0, 0, 30, 6, "#FFFFFF");
                Stripe(canvas, 0, 0, 60, 30, 2, "#C8102E");
                Stripe(canvas, 60, 0, 0, 30, 2, "#C8102E");
                Fill(canvas, 25, 0, 10, 30, "#FFFFFF");
                Fill(canvas, 0, 10, 60, 10, "#FFFFFF");
                Fill(canvas, 27, 0, 6, 30, "#C8102E");
                Fill(canvas, 0, 12, 60, 6, "#C8102E");
                break;
        }

        return new Border
        {
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox { Width = height * 1.5, Height = height, Stretch = Stretch.Fill, Child = canvas },
        };
    }

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    static void Fill(Canvas canvas, double x, double y, double width, double height, string hex)
    {
        var rectangle = new Rectangle { Width = width, Height = height, Fill = Brush(hex) };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        canvas.Children.Add(rectangle);
    }

    static void Stripe(Canvas canvas, double x1, double y1, double x2, double y2, double thickness, string hex) =>
        canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brush(hex), StrokeThickness = thickness });
}
