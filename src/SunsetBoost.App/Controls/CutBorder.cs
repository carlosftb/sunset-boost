using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SunsetBoost.App.Controls;

public enum CutStyle { Corners, TopRight, Parallelogram, BottomLeft }

/// <summary>Border com cantos cortados na diagonal (o visual de "painel recortado" do tema).</summary>
public class CutBorder : Border
{
    public static readonly DependencyProperty CutSizeProperty = DependencyProperty.Register(
        nameof(CutSize), typeof(double), typeof(CutBorder),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
        nameof(Cut), typeof(CutStyle), typeof(CutBorder),
        new FrameworkPropertyMetadata(CutStyle.Corners, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double CutSize { get => (double)GetValue(CutSizeProperty); set => SetValue(CutSizeProperty, value); }
    public CutStyle Cut { get => (CutStyle)GetValue(CutProperty); set => SetValue(CutProperty, value); }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        Clip = BuildClip(size.Width, size.Height, Math.Min(CutSize, Math.Min(size.Width, size.Height) / 2), Cut);
        return size;
    }

    private static Geometry BuildClip(double w, double h, double c, CutStyle style)
    {
        Point[] pts = style switch
        {
            CutStyle.TopRight => new Point[] { new(0, 0), new(w - c, 0), new(w, c), new(w, h), new(0, h) },
            CutStyle.Parallelogram => new Point[] { new(c, 0), new(w, 0), new(w - c, h), new(0, h) },
            CutStyle.BottomLeft => new Point[] { new(0, 0), new(w, 0), new(w, h), new(c, h), new(0, h - c) },
            _ => new Point[] { new(c, 0), new(w, 0), new(w, h - c), new(w - c, h), new(0, h), new(0, c) },
        };
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            ctx.PolyLineTo(pts.Skip(1).ToArray(), false, false);
        }
        g.Freeze();
        return g;
    }
}
