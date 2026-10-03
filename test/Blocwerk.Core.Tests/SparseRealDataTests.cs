// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Sparse;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Real data (the owner's folder, never committed; the test does nothing without it): the copy wall's markerless run-3
/// sparse model against what its photo-real view gave. Volumes and hold protrusion measured in the sparse points should
/// find the splat's volumes and agree with the splat's protrusion to about the sparse points' noise. Needs
/// <c>BLOCWERK_DATA_DIR</c> with <c>markerless/run3/sparse.zip</c> and <c>markerless/run3/copy-wall/{model,volumes,holds}.json</c>
/// (the active model, its splat-derived volumes and the placed holds, exported from the database).
/// </summary>
public class SparseRealDataTests(ITestOutputHelper output)
{
    [Fact]
    public void CopyWall_Run3SparsePoints_FindTheSplatVolumes_AndMeasureTheHolds()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_DATA_DIR") is { } d ? Path.Combine(d, "markerless", "run3") : null;
        if (dir is null || !File.Exists(Path.Combine(dir, "sparse.zip")) || !File.Exists(Path.Combine(dir, "copy-wall", "holds.json")))
        {
            return;
        }

        var modelJson = File.ReadAllText(Path.Combine(dir, "copy-wall", "model.json"));
        var cloud = SparseCloudFile.Read(SparseCloudFile.Write(ColmapSparseReader.Read(File.ReadAllBytes(Path.Combine(dir, "sparse.zip")))));
        var alignment = SparseWorldAlignment.Fit(cloud.PhotoCentres, SolvedCamera.ParseAll(modelJson));
        Assert.NotNull(alignment);
        var points = SparseWorldAlignment.WorldPoints(cloud, alignment);
        output.WriteLine($"cloud {cloud.Count} points, {cloud.PhotoCentres.Count} photos; fit on {alignment.Used} (rms {alignment.RmsMm} mm, s {alignment.Similarity.Scale:F3}); {points.Count} kept");

        var doc = WallGeometryDocument.Parse(modelJson);
        var frames = new Dictionary<string, FacetFrame>(StringComparer.Ordinal);
        var extents = new Dictionary<string, PlaneRectMm>(StringComparer.Ordinal);
        foreach (var f in doc.Segments.SelectMany(s => s.Facets).Where(f => !string.IsNullOrEmpty(f.Id) && f.ExtentMm is not null))
        {
            frames[f.Id!] = FacetFrame.From(f)!;
            extents[f.Id!] = f.ExtentMm!.Value;
        }

        var holds = ReadHolds(Path.Combine(dir, "copy-wall", "holds.json")).Where(h => frames.ContainsKey(h.FacetId)).ToList();
        var splatVolumes = ReadVolumes(Path.Combine(dir, "copy-wall", "volumes.json"));
        var known = holds.GroupBy(h => h.FacetId, StringComparer.Ordinal).ToDictionary(
            g => g.Key, g => g.Select(h => new KnownHoldEllipse(h.A, h.B, h.W / 2, h.H / 2)).ToList(), StringComparer.Ordinal);

        var found = VolumeDetector.Detect(points, frames, extents, known, VolumeDetectionOptions.Sparse);
        var accepted = found.Where(v => v.IsAccepted).ToList();
        foreach (var v in found)
        {
            output.WriteLine($"  {v.Status,-26} facet {v.FacetId} area {v.AreaM2:F3} m² height {v.HeightMm:F0} mm at {Centre(v.Footprint)}");
        }

        foreach (var s in splatVolumes)
        {
            var hit = accepted.Any(v => Overlaps(v.FacetId, v.Footprint, s.FacetId, s.Footprint));
            output.WriteLine($"  splat volume facet {s.FacetId} at {Centre(s.Footprint)} area {PlanePolygon.Area(s.Footprint) / 1e6:F3} m²: {(hit ? "found" : "missed")}");
        }

        var matched = splatVolumes.Count(s => accepted.Any(v => Overlaps(v.FacetId, v.Footprint, s.FacetId, s.Footprint)));
        var extras = accepted.Count(v => !splatVolumes.Any(s => Overlaps(v.FacetId, v.Footprint, s.FacetId, s.Footprint)));
        var onSplat = holds.Count(h => splatVolumes.Any(v => v.FacetId == h.FacetId && PlanePolygon.Contains(v.Footprint, (h.A, h.B))));
        var onSparse = holds.Count(h => accepted.Any(v => v.FacetId == h.FacetId && PlanePolygon.Contains(v.Footprint, (h.A, h.B))));
        output.WriteLine($"volumes: {accepted.Count} from sparse, {splatVolumes.Count} from the splat, {matched} of those found, {extras} not in the splat; holds on volumes {onSparse} sparse vs {onSplat} splat");

        var targets = holds.Select(h => new ProtrusionHold(h.Id, h.FacetId, h.A, h.B, h.Outline, h.Key)).ToList();
        var cameras = SolvedCamera.ParseAll(modelJson).Select(c => c.Centre).ToList();
        var measured = HoldProtrusionEstimator.Measure(points, frames, targets, cameras, extents, HoldProtrusionTuning.Sparse);
        var pairs = holds.Where(h => h.Splat is { Source: HoldProtrusionSource.Splat } && measured.TryGetValue(h.Id, out var m) && m.Source == HoldProtrusionSource.Sparse)
            .Select(h => (Splat: h.Splat!, Sparse: measured[h.Id]))
            .ToList();
        var diffs = pairs.Select(p => Math.Abs(p.Sparse.HeightMm - p.Splat.HeightMm)).Order().ToList();
        var signed = pairs.Select(p => p.Sparse.HeightMm - p.Splat.HeightMm).Order().ToList();
        var splatMeasured = holds.Count(h => h.Splat is { Source: HoldProtrusionSource.Splat });
        output.WriteLine(
            $"protrusion: {measured.Values.Count(p => p.Source == HoldProtrusionSource.Sparse)} of {holds.Count} measured in sparse points ({splatMeasured} in the splat); "
            + $"{pairs.Count} compared, median |diff| {Median(diffs):F1} mm, p90 {Quantile(diffs, 0.9):F1} mm, median diff {Median(signed):F1} mm");

        Assert.True(alignment.RmsMm < 5, "the run-3 model is this sparse model's own feature solve");
        Assert.True(matched >= 5, $"only {matched} of the splat's {splatVolumes.Count} volumes found");
        Assert.True(pairs.Count > 100 && Median(diffs) < 8, $"median |diff| {Median(diffs):F1} mm over {pairs.Count} holds");
    }

    /// <summary>Same facet, and their overlap covers at least 30 % of the smaller one.</summary>
    private static bool Overlaps(string facetA, IReadOnlyList<(double A, double B)> a, string facetB, IReadOnlyList<(double A, double B)> b)
    {
        if (facetA != facetB)
        {
            return false;
        }

        var common = PlanePolygon.Area(PlanePolygon.ClipConvex(PlanePolygon.ConvexHull(a), PlanePolygon.ConvexHull(b)));
        return common >= 0.3 * Math.Min(PlanePolygon.Area(a), PlanePolygon.Area(b));
    }

    private static (double A, double B) Centre(IReadOnlyList<(double A, double B)> ring) =>
        (Math.Round(ring.Average(p => p.A)), Math.Round(ring.Average(p => p.B)));

    private static double Median(List<double> sorted) => Quantile(sorted, 0.5);

    private static double Quantile(List<double> sorted, double q) => sorted.Count == 0 ? double.NaN : sorted[(int)(q * (sorted.Count - 1))];

    private static List<(string FacetId, List<(double A, double B)> Footprint)> ReadVolumes(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(v => (
            v.GetProperty("facetId").GetString()!,
            v.GetProperty("footprint").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList())).ToList();
    }

    private static List<RealHold> ReadHolds(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<RealHold>();
        foreach (var h in doc.RootElement.EnumerateArray())
        {
            var w = h.GetProperty("w").ValueKind == JsonValueKind.Number ? h.GetProperty("w").GetDouble() : 60;
            var ht = h.GetProperty("h").ValueKind == JsonValueKind.Number ? h.GetProperty("h").GetDouble() : 60;
            var splat = h.GetProperty("protrusion").GetString() is { } pj ? JsonSerializer.Deserialize<HoldProtrusion>(pj, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
            var outline = Outline(h.GetProperty("footprint").GetString()) ?? Ellipse(w / 2, ht / 2);
            result.Add(new RealHold(
                h.GetProperty("id").GetGuid(), h.GetProperty("facetId").GetString()!, h.GetProperty("a").GetDouble(), h.GetProperty("b").GetDouble(),
                w, ht, outline, splat?.OutlineKey ?? string.Empty, splat));
        }

        return result;
    }

    private static List<double[]>? Outline(string? footprint)
    {
        if (footprint is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(footprint);
        return doc.RootElement.TryGetProperty("outline", out var o)
            ? o.EnumerateArray().Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() }).ToList()
            : null;
    }

    private static List<double[]> Ellipse(double ra, double rb) =>
        Enumerable.Range(0, 16).Select(i => new[] { ra * Math.Cos(i * Math.PI / 8), rb * Math.Sin(i * Math.PI / 8) }).ToList();

    private sealed record RealHold(Guid Id, string FacetId, double A, double B, double W, double H, List<double[]> Outline, string Key, HoldProtrusion? Splat);
}
