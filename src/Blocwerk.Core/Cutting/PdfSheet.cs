using System.Globalization;
using SkiaSharp;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Thin drawing helper over an <see cref="SKCanvas"/> for engineering-style sheets:
/// consistent line weights, linear dimensions with extension lines and arrowheads,
/// angle callouts and text. Colours are plain black/greys so it prints cleanly.
/// </summary>
public sealed class PdfSheet
{
    private static readonly SKColor InkColor = new(0x1a, 0x1a, 0x2e);
    private static readonly SKColor Accent = new(0xe9, 0x45, 0x60);
    private static readonly SKColor Dim = new(0x55, 0x55, 0x66);
    private static readonly SKColor Fill = new(0xf0, 0xf0, 0xf3);

    private readonly SKCanvas canvas;

    public PdfSheet(SKCanvas canvas)
    {
        this.canvas = canvas;
    }

    public void Outline(Point2D[] pts, bool fill)
    {
        using var path = ToPath(pts);
        if (fill)
        {
            using var f = new SKPaint { Color = Fill, Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawPath(path, f);
        }

        using var stroke = new SKPaint { Color = InkColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f, IsAntialias = true };
        canvas.DrawPath(path, stroke);
    }

    public void Line(Point2D a, Point2D b, float width, SKColor color, float[]? dash = null)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true };
        if (dash is not null)
        {
            paint.PathEffect = SKPathEffect.CreateDash(dash, 0);
        }

        canvas.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, paint);
    }

    public void InkLine(Point2D a, Point2D b) => Line(a, b, 1.1f, InkColor);

    /// <summary>
    /// A linear dimension between two points, offset perpendicular by <paramref name="offset"/>
    /// screen units, with extension lines, arrowheads and a centred label.
    /// </summary>
    public void Dimension(Point2D a, Point2D b, double offset, string label)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-6)
        {
            return;
        }

        var nx = -dy / len * offset;
        var ny = dx / len * offset;

        var a2 = new Point2D(a.X + nx, a.Y + ny);
        var b2 = new Point2D(b.X + nx, b.Y + ny);

        Line(a, a2, 0.6f, Dim);
        Line(b, b2, 0.6f, Dim);
        Line(a2, b2, 0.7f, Dim);
        Arrow(b2, a2);
        Arrow(a2, b2);

        var mid = new Point2D((a2.X + b2.X) / 2.0, (a2.Y + b2.Y) / 2.0);
        var up = new Point2D(mid.X - dy / len * 7, mid.Y + dx / len * 7);
        Text(label, up, 9.5f, Dim, SKTextAlign.Center);
    }

    /// <summary>A compact dimension: just the line with arrowheads at both ends and a label, no extension lines.</summary>
    public void DimLine(Point2D a, Point2D b, Point2D labelAt, string label, SKTextAlign align)
    {
        Line(a, b, 0.6f, Dim);
        Arrow(b, a);
        Arrow(a, b);
        Text(label, labelAt, 8f, Dim, align);
    }

    public void AngleLabel(Point2D vertex, Point2D toward, string label)
    {
        var dx = toward.X - vertex.X;
        var dy = toward.Y - vertex.Y;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-6)
        {
            return;
        }

        var at = new Point2D(vertex.X + dx / len * 16, vertex.Y + dy / len * 16);
        Text(label, at, 8.5f, Accent, SKTextAlign.Center);
    }

    public void Text(string text, Point2D at, float size, SKColor color, SKTextAlign align, bool bold = false)
    {
        using var font = new SKFont(SKTypeface.FromFamilyName("sans-serif", bold ? SKFontStyle.Bold : SKFontStyle.Normal), size);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, (float)at.X, (float)at.Y + (size * 0.35f), align, font, paint);
    }

    public void Ink(string text, Point2D at, float size, SKTextAlign align, bool bold = false) =>
        Text(text, at, size, InkColor, align, bold);

    public void Muted(string text, Point2D at, float size, SKTextAlign align) =>
        Text(text, at, size, Dim, align);

    public void FrameBox(SKRect rect, string caption)
    {
        using var stroke = new SKPaint { Color = Dim, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, IsAntialias = true };
        canvas.DrawRect(rect, stroke);
        Text(caption, new Point2D(rect.Left + 4, rect.Top + 9), 8f, Dim, SKTextAlign.Left);
    }

    public static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private void Arrow(Point2D from, Point2D tip)
    {
        var dx = tip.X - from.X;
        var dy = tip.Y - from.Y;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-6)
        {
            return;
        }

        var ux = dx / len;
        var uy = dy / len;
        const double size = 4.5;
        var left = new Point2D(tip.X - (ux * size) - (uy * size * 0.5), tip.Y - (uy * size) + (ux * size * 0.5));
        var right = new Point2D(tip.X - (ux * size) + (uy * size * 0.5), tip.Y - (uy * size) - (ux * size * 0.5));
        Line(tip, left, 0.7f, Dim);
        Line(tip, right, 0.7f, Dim);
    }

    private static SKPath ToPath(Point2D[] pts)
    {
        var path = new SKPath();
        path.MoveTo((float)pts[0].X, (float)pts[0].Y);
        for (var i = 1; i < pts.Length; i++)
        {
            path.LineTo((float)pts[i].X, (float)pts[i].Y);
        }

        path.Close();
        return path;
    }
}
