// <copyright file="ShapeFallbackDiagnostics.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// Why does the outliner fall back to the circle on a real capture? Reads <c>BLOCWERK_SHAPEDIAG_DIR</c>
/// (p0.jpg/holds0.csv, p1.jpg/holds1.csv: "x,y,radius,…" rows) and tallies the method, the confidence band and
/// the reason for every fallback. Skipped without the variable; never committed data.
/// </summary>
public class ShapeFallbackDiagnostics(ITestOutputHelper output)
{
    [SkippableFact]
    public void Tally_OnAnExportedCapture()
    {
        string? dir = Environment.GetEnvironmentVariable("BLOCWERK_SHAPEDIAG_DIR");
        Skip.If(string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir!, "holds0.csv")), "Set BLOCWERK_SHAPEDIAG_DIR.");
        log = output.WriteLine;
        var methods = new Dictionary<string, int>();
        var bands = new Dictionary<string, int>();
        var reasons = new Dictionary<string, int>();
        var colouredReasons = new Dictionary<string, int>();
        int coloured = 0, colouredFallback = 0;
        int edgeDumps = 0;
        var perPanel = new Dictionary<int, (int Fallback, int All)>();
        for (int c = 0; c < 2; c++)
        {
            using Mat img = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(dir!, $"p{c}.jpg")), ImreadModes.Color);
            using var session = new OpenCvHoldOutlineSession(img, ownsImage: false);
            foreach (var line in File.ReadAllLines(Path.Combine(dir!, $"holds{c}.csv")))
            {
                var f = line.Split(',');
                if (f[3] != "t" || D(f[0]) is < 0 or >= 1 || D(f[1]) is < 0 or >= 1)
                {
                    continue;
                }

                var seed = new HoldSeed(D(f[0]), D(f[1]), D(f[2]));
                var r = session.Outline(seed);
                Bump(methods, r.Method.ToString());
                var pp = perPanel.GetValueOrDefault(c);
                perPanel[c] = (pp.Fallback + (r.Method == HoldOutlineMethod.CircleFallback ? 1 : 0), pp.All + 1);
                if (Environment.GetEnvironmentVariable("BLOCWERK_SHAPEDIAG_OUT") is { Length: > 0 } o)
                {
                    Overlay(o, c, img, seed, r.Method);
                }

                Bump(bands, r.Confidence < 0.2 ? "<0.2" : r.Confidence < 0.4 ? "0.2-0.4" : r.Confidence < 0.7 ? "0.4-0.7" : ">=0.7");
                bool isColoured = CoreChroma(img, seed) >= ColouredChroma;
                coloured += isColoured ? 1 : 0;
                if (r.Method == HoldOutlineMethod.CircleFallback && isColoured)
                {
                    colouredFallback++;
                    var cw = Why(img, seed);
                    Bump(colouredReasons, cw);
                    Dump(img, seed, "col-" + cw, colouredReasons[cw], c);
                }

                if (r.Method == HoldOutlineMethod.GrabCut && r.Confidence < 0.6 && ++edgeDumps <= 16)
                {
                    DumpOutline(img, seed, r, edgeDumps, c);
                }

                if (r.Method == HoldOutlineMethod.CircleFallback)
                {
                    var why = Why(img, seed);
                    Bump(reasons, why);
                    Dump(img, seed, "all-" + why, reasons[why], c);
                }
            }
        }

        output.WriteLine("per panel fallback " + string.Join(", ", perPanel.Select(kv => $"p{kv.Key}={kv.Value.Fallback}/{kv.Value.All}")));
        output.WriteLine("methods " + string.Join(", ", methods.Select(kv => $"{kv.Key}={kv.Value}")));
        output.WriteLine("bands " + string.Join(", ", bands.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        output.WriteLine($"clearly coloured holds {coloured}, circle fallback {colouredFallback}: "
            + string.Join(", ", colouredReasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        output.WriteLine("fallback reasons " + string.Join(", ", reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
    }

    /// <summary>Lab chroma at or above which a hold core counts as clearly coloured.</summary>
    private const double ColouredChroma = 25;

    /// <summary>Median chroma (√(a²+b²)) of the inner 0.35 r of the seed — independent of the wall model.</summary>
    private static double CoreChroma(Mat img, HoldSeed seed)
    {
        using var crop = OutlineCrop.Create(img, seed);
        if (crop is null)
        {
            return 0;
        }

        var px = CropPixels.From(crop.Bgr);
        var values = new List<double>();
        for (int y = 0; y < px.Height; y++)
        {
            for (int x = 0; x < px.Width; x++)
            {
                if (LocalColourModel.Normalized(crop, x, y) <= 0.35)
                {
                    int i = (y * px.Width) + x;
                    values.Add(Math.Sqrt((px.A[i] * px.A[i]) + (px.B[i] * px.B[i])));
                }
            }
        }

        values.Sort();
        return values.Count == 0 ? 0 : values[values.Count / 2];
    }

    private static Action<string>? log;

    private static string Why(Mat img, HoldSeed seed)
    {
        using var crop = OutlineCrop.Create(img, seed);
        if (crop is null)
        {
            return "off-image";
        }

        var px = CropPixels.From(crop.Bgr);
        var model = LocalColourModel.Sample(px, crop);
        if (model is null)
        {
            return "no-model";
        }

        if (model.Contrast < OpenCvHoldOutlineSession.MinContrast)
        {
            return model.CoreIsWall ? "low-contrast(core=wall)" : "low-contrast";
        }

        using Mat raw = ColourDistanceSegmenter.Segment(px, model, 0.45);
        int before = Cv2.CountNonZero(raw);
        MaskOps.Clean(raw, crop.Radius);
        var contour = MaskOps.SeedContour(raw, crop);
        if (contour is null)
        {
            log?.Invoke($"no-contour r={crop.Radius:F0} wall=({model.Wall[0]:F0},{model.Wall[1]:F0},{model.Wall[2]:F0}) hold=({model.Hold[0]:F0},{model.Hold[1]:F0},{model.Hold[2]:F0}) c={model.Contrast:F1} coreIsWall={model.CoreIsWall} mask {before}->{Cv2.CountNonZero(raw)} of {px.L.Length}");
            return "no-contour";
        }

        var (ok, ratio, reason) = MaskOps.Assess(contour, crop, MaskOps.MinAreaRatio);
        if (!ok)
        {
            return reason + (ratio > 1 ? ">" : "<");
        }

        using Mat filled = MaskOps.Fill(contour, crop.Bgr.Width, crop.Bgr.Height);
        float share = model.WallLikeShare(px, CropPixels.ToBytes(filled), (float)OpenCvHoldOutlineSession.MinContrast);
        return share > OpenCvHoldOutlineSession.MaxWallLikeShare ? "wall-share" : "other(grabcut/2nd)";
    }

    private static void DumpOutline(Mat img, HoldSeed seed, HoldOutlineResult r, int n, int panel)
    {
        string? outDir = Environment.GetEnvironmentVariable("BLOCWERK_SHAPEDIAG_OUT");
        if (string.IsNullOrEmpty(outDir))
        {
            return;
        }

        int cx = (int)(seed.X * img.Width), cy = (int)(seed.Y * img.Height), rr = (int)(seed.Radius * Math.Max(img.Width, img.Height));
        int half = Math.Max(3 * rr, 40);
        var roi = new Rect(Math.Max(0, cx - half), Math.Max(0, cy - half), 0, 0);
        roi.Width = Math.Min(img.Width, cx + half) - roi.X;
        roi.Height = Math.Min(img.Height, cy + half) - roi.Y;
        using var view = new Mat(img, roi).Clone();
        var pts = r.Polygon.Select(p => new Point((p.X * img.Width) - roi.X, (p.Y * img.Height) - roi.Y)).ToArray();
        Cv2.Polylines(view, new[] { pts }, true, Scalar.Magenta, 2);
        Directory.CreateDirectory(outDir!);
        Cv2.ImWrite(Path.Combine(outDir!, $"edge-{n}-p{panel}-c{r.Confidence:F2}.png"), view);
    }

    private static void Dump(Mat img, HoldSeed seed, string why, int n, int panel)
    {
        string? outDir = Environment.GetEnvironmentVariable("BLOCWERK_SHAPEDIAG_OUT");
        if (string.IsNullOrEmpty(outDir) || n > 8)
        {
            return;
        }

        int cx = (int)(seed.X * img.Width), cy = (int)(seed.Y * img.Height), r = (int)(seed.Radius * Math.Max(img.Width, img.Height));
        int half = Math.Max(3 * r, 40);
        var roi = new Rect(Math.Max(0, cx - half), Math.Max(0, cy - half), 0, 0);
        roi.Width = Math.Min(img.Width, cx + half) - roi.X;
        roi.Height = Math.Min(img.Height, cy + half) - roi.Y;
        using var view = new Mat(img, roi).Clone();
        Cv2.Circle(view, new Point(cx - roi.X, cy - roi.Y), r, Scalar.Lime, 1);
        Directory.CreateDirectory(outDir!);
        Cv2.ImWrite(Path.Combine(outDir!, $"{why.Replace('(', '_').Replace(')', '_').Replace('>', 'G').Replace('<', 'L').Replace('=', '_')}-{n}-p{panel}-r{r}.png"), view);
    }

    private static readonly Dictionary<int, Mat> Overlays = [];

    private static void Overlay(string outDir, int panel, Mat img, HoldSeed seed, HoldOutlineMethod method)
    {
        if (!Overlays.TryGetValue(panel, out var canvas))
        {
            canvas = new Mat();
            Cv2.Resize(img, canvas, new Size(img.Width / 3, img.Height / 3));
            Overlays[panel] = canvas;
        }

        var colour = method == HoldOutlineMethod.CircleFallback ? Scalar.Red : Scalar.Lime;
        Cv2.Circle(canvas, new Point(seed.X * canvas.Width, seed.Y * canvas.Height), (int)(seed.Radius * Math.Max(canvas.Width, canvas.Height)), colour, 2);
        Cv2.ImWrite(Path.Combine(outDir, $"overlay-p{panel}.jpg"), canvas);
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
}
