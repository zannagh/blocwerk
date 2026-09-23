// <copyright file="MarkerPhotoSim.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.HoldDetection.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.MarkerPlanning;

/// <summary>How a rendered marker page is dressed up to look like a marker screwed to a wall.</summary>
/// <param name="HeadMm">Screw heads painted dark at the plan's hole positions; null = none.</param>
/// <param name="WallGrey">Mean grey of the textured wall painted outside every cut line; null = white paper.</param>
/// <param name="CutBorderMm">Where the paper ends (black edge to cut line); null = the printed cut line.</param>
/// <param name="BlurSigmaPx">Optical blur applied last; 0 = none.</param>
public sealed record PhotoDressing(double? HeadMm, double? WallGrey, double? CutBorderMm, double BlurSigmaPx);

/// <summary>
/// Renders marker pages with the PDF's own drawing code and measures the app's detector on them: per
/// marker the largest distance of a detected corner from where the PDF drew it (px), or NaN when the
/// marker did not decode.
/// </summary>
internal static class MarkerPhotoSim
{
    /// <summary>Grey level of the simulated screw heads (dark zinc / black oxide).</summary>
    public const double HeadGrey = 50;

    /// <summary>Texture amplitude of the wall (± grey levels, before smoothing).</summary>
    private const int WallNoise = 45;

    /// <summary>Margin added around every rendered page, px.</summary>
    private const int FramePx = 24;

    public static Dictionary<int, double> CornerErrors(MarkerPlan plan, float dpi, PhotoDressing dressing, MarkerDetectionOptions options)
    {
        var pxPerMm = dpi / 25.4;
        var printed = MarkerPlanPdf.PrintedMarkers(plan);
        var holes = plan.Print?.MountingHoles;
        var errors = new Dictionary<int, double>();
        foreach (var pageIndex in printed.Select(p => p.PageIndex).Distinct())
        {
            var onPage = printed.Where(p => p.PageIndex == pageIndex).ToList();
            using var gray = Cv2.ImDecode(MarkerPlanPdf.RasterizePage(plan, "holes test", pageIndex, dpi), ImreadModes.Grayscale);
            if (dressing.WallGrey is { } wall)
            {
                PaintWall(gray, onPage, wall, dressing.CutBorderMm, pxPerMm, pageIndex);
            }

            if (dressing.HeadMm is { } head && holes is not null)
            {
                PaintHeads(gray, onPage, holes with { ScrewHeadDiameterMm = head }, pxPerMm);
            }

            if (dressing.BlurSigmaPx > 0)
            {
                Cv2.GaussianBlur(gray, gray, new Size(0, 0), dressing.BlurSigmaPx);
            }

            // A photo never ends at the cut line: pad the page so no edge profile runs off the image.
            using var framed = new Mat();
            Cv2.CopyMakeBorder(gray, framed, FramePx, FramePx, FramePx, FramePx, BorderTypes.Replicate);
            var result = ArucoMarkerDetectionService.Detect(framed, options);
            foreach (var truth in onPage)
            {
                var found = result.Markers.Where(m => m.Id == truth.Id).ToList();
                errors[truth.Id] = found.Count == 1 ? MaxCornerError(found[0], truth, pxPerMm) : double.NaN;
            }
        }

        return errors;
    }

    /// <summary>A plan of equally spaced markers of the given sizes on one board (ids in order).</summary>
    public static MarkerPlan TestPlan(IReadOnlyList<double> sizes, MountingHoles? holes)
    {
        var markers = sizes.Select((size, id) => new PlanMarker(id, 0, 200 + (id * 300), 400, size, MarkerRole.Corner)).ToList();
        return new MarkerPlan(
            MarkerPlan.CurrentSchemaVersion,
            ArucoDict4X4.DictionaryName,
            MarkerCameraPresets.Create(MarkerCameraPresets.Phone1X, 2000),
            [new PlanSegment(0, "test board", SegmentShape.Rectangle, 3000, 1000, TriangleCorner.BottomLeft, 0, 0, null)],
            markers,
            holes is null ? null : new PrintOptions(holes));
    }

    /// <summary>
    /// Everything outside the cut boxes becomes a blotchy textured wall; the paper edge is anti-aliased by
    /// exact pixel coverage so a sub-pixel border stays sub-pixel.
    /// </summary>
    private static void PaintWall(Mat gray, IReadOnlyList<PrintedMarker> markers, double wallGrey, double? borderMm, double pxPerMm, int seed)
    {
        using var texture = new Mat(gray.Rows, gray.Cols, MatType.CV_8UC1);
        var random = new Random(1234 + seed);
        var noise = new byte[gray.Rows * gray.Cols];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (byte)Math.Clamp(wallGrey + random.Next(-WallNoise, WallNoise + 1), 0, 255);
        }

        texture.SetArray(noise);
        Cv2.GaussianBlur(texture, texture, new Size(0, 0), 1.2);
        var boxes = markers.Select(m =>
        {
            var b = borderMm ?? m.BorderMm;
            return (L: (m.LeftMm - b) * pxPerMm, T: (m.TopMm - b) * pxPerMm, R: (m.LeftMm + m.SizeMm + b) * pxPerMm, B: (m.TopMm + m.SizeMm + b) * pxPerMm);
        }).ToList();
        for (var y = 0; y < gray.Rows; y++)
        {
            for (var x = 0; x < gray.Cols; x++)
            {
                var paper = boxes.Sum(b => Overlap(x, b.L, b.R) * Overlap(y, b.T, b.B));
                var wall = texture.At<byte>(y, x);
                gray.Set(y, x, (byte)Math.Round((paper * gray.At<byte>(y, x)) + ((1 - Math.Min(1, paper)) * wall)));
            }
        }
    }

    private static double Overlap(int pixel, double from, double to) => Math.Clamp(Math.Min(pixel + 1, to) - Math.Max(pixel, from), 0, 1);

    private static void PaintHeads(Mat gray, IEnumerable<PrintedMarker> markers, MountingHoles holes, double pxPerMm)
    {
        const int Shift = 4;
        const double Scale = 1 << Shift;
        foreach (var m in markers)
        {
            foreach (var (x, y) in MountingHoleLayout.HoleCentres(m.LeftMm, m.TopMm, m.SizeMm, holes))
            {
                // Page px e sits at image px e − 0.5 (pixel centres at +0.5).
                var centre = new Point((int)Math.Round(((x * pxPerMm) - 0.5) * Scale), (int)Math.Round(((y * pxPerMm) - 0.5) * Scale));
                var radius = (int)Math.Round(holes.ScrewHeadDiameterMm / 2 * pxPerMm * Scale);
                Cv2.Circle(gray, centre, radius, new Scalar(HeadGrey), -1, LineTypes.AntiAlias, Shift);
            }
        }
    }

    /// <summary>Pixel centres sit at +0.5 in page space, so a drawn edge at page px e is at e − 0.5 (plus the frame).</summary>
    private static double MaxCornerError(DetectedMarker found, PrintedMarker truth, double pxPerMm)
    {
        var l = (truth.LeftMm * pxPerMm) - 0.5 + FramePx;
        var t = (truth.TopMm * pxPerMm) - 0.5 + FramePx;
        var s = truth.SizeMm * pxPerMm;
        (double X, double Y)[] expected = [(l, t), (l + s, t), (l + s, t + s), (l, t + s)];
        return found.CornersPx.Select((c, i) => Math.Sqrt(Math.Pow(c.X - expected[i].X, 2) + Math.Pow(c.Y - expected[i].Y, 2))).Max();
    }
}
