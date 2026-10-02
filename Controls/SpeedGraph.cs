using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GameShelf.Services;

namespace GameShelf.Controls;

/// <summary>Live download speed over time: a filled curve, newest sample on the right, with the peak speed noted.</summary>
internal sealed class SpeedGraph : FrameworkElement
{
    const int MaxSamples = 90;
    const double Padding = 8, LabelHeight = 16;

    static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1b, 0x19, 0x17)));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8a, 0x83, 0x78)));
    static readonly Brush FillBrush = Frozen(new LinearGradientBrush(
        Color.FromArgb(150, 0x5c, 0xc8, 0x4a), Color.FromArgb(10, 0x5c, 0xc8, 0x4a), 90));
    static readonly Pen LinePen = new(Frozen(new SolidColorBrush(Color.FromRgb(0x5c, 0xc8, 0x4a))), 1.8)
        { LineJoin = PenLineJoin.Round };

    readonly List<double> samples = new();

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public void Add(double bytesPerSecond)
    {
        samples.Add(bytesPerSecond);
        if (samples.Count > MaxSamples) samples.RemoveAt(0);
        InvalidateVisual();
    }

    public void Clear()
    {
        samples.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        double width = ActualWidth, height = ActualHeight;
        context.DrawRoundedRectangle(BackgroundBrush, null, new Rect(0, 0, width, height), 6, 6);
        if (samples.Count < 2) return;

        double peak = Math.Max(samples.Max(), 1);
        double step = (width - 2 * Padding) / (MaxSamples - 1);
        double plotTop = Padding + LabelHeight, plotBottom = height - Padding;
        Point PointAt(int i) => new(
            width - Padding - (samples.Count - 1 - i) * step,
            plotBottom - samples[i] / peak * (plotBottom - plotTop));

        var curve = new StreamGeometry();
        using (var geometry = curve.Open())
        {
            geometry.BeginFigure(new Point(PointAt(0).X, plotBottom), isFilled: true, isClosed: true);
            for (int i = 0; i < samples.Count; i++) geometry.LineTo(PointAt(i), isStroked: false, isSmoothJoin: false);
            geometry.LineTo(new Point(PointAt(samples.Count - 1).X, plotBottom), isStroked: false, isSmoothJoin: false);
        }
        curve.Freeze();
        context.DrawGeometry(FillBrush, null, curve);

        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            geometry.BeginFigure(PointAt(0), isFilled: false, isClosed: false);
            for (int i = 1; i < samples.Count; i++) geometry.LineTo(PointAt(i), isStroked: true, isSmoothJoin: true);
        }
        line.Freeze();
        context.DrawGeometry(null, LinePen, line);

        var label = new FormattedText(Loc.T("Peak {0}", DisplayFormat.Speed((long)peak)), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(label, new Point(Padding + 2, 5));
    }
}
