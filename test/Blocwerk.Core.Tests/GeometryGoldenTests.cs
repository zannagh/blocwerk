// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The geometry kernel's golden cases (<c>test/geometry-golden/*.json</c>), shared with the Python wall-geometry tests
/// (<c>docker/wall-geometry/tests/test_geometry_golden.py</c>): both languages must give the same answers on the same
/// walls. Each case is a small wall-geometry document, the expected occluder region per facet (its seam cuts and fold
/// clips as unit half-planes) and queries: point in a facet's shape, line of sight blocked by one facet or by the scene,
/// and visible (facing and not blocked). See <c>docs/geometry-kernel.md</c>.
/// </summary>
public sealed class GeometryGoldenTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "GeometryGolden");

    public static TheoryData<string> Cases() => new(Directory.GetFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension).Order()!);

    [Fact]
    public void ThereIsAGoldenCasePerWallKind()
    {
        var cases = Directory.GetFiles(Dir, "*.json").Select(Load).ToList();

        Assert.True(cases.Count >= 8);
        Assert.True(cases.Sum(c => c.Queries.Count) >= 40);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OccluderRegions_MatchTheGoldenCuts(string name)
    {
        var golden = Load(Path.Combine(Dir, name + ".json"));
        var occluders = golden.Occluders();

        Assert.Equal(golden.Cuts.Keys.Order(), occluders.Keys.Order());
        foreach (var (id, expected) in golden.Cuts)
        {
            var got = Sorted(occluders[id].Cuts.Select(c => new[] { c.Alpha, c.Beta, c.Gamma }));
            var want = Sorted(expected);
            Assert.True(got.Count == want.Count, $"{id}: {Text(got)} != {Text(want)}");
            for (var i = 0; i < got.Count; i++)
            {
                Assert.True(got[i].Zip(want[i]).All(p => Math.Abs(p.First - p.Second) <= 2e-3), $"{id}: {Text(got)} != {Text(want)}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Queries_GiveTheGoldenAnswers(string name)
    {
        var golden = Load(Path.Combine(Dir, name + ".json"));
        var occluders = golden.Occluders();

        var wrong = golden.Queries
            .Select((q, k) => (q, k))
            .Where(x => Answer(x.q, occluders) != x.q.GetProperty("expected").GetBoolean())
            .Select(x => $"{x.q.GetProperty("kind").GetString()} #{x.k}: expected {x.q.GetProperty("expected")} ({x.q.GetProperty("why")})")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    private static bool Answer(JsonElement q, IReadOnlyDictionary<string, CoverageOccluder> occluders)
    {
        var inset = q.GetProperty("insetMm").GetDouble();
        switch (q.GetProperty("kind").GetString())
        {
            case "inShape":
                var ab = Vector(q, "ab");
                return occluders[q.GetProperty("facet").GetString()!].Contains(ab[0], ab[1], inset);
            case "blockedBy":
                return occluders[q.GetProperty("occluder").GetString()!].Blocks(Vector(q, "camera"), Vector(q, "target"), inset);
            case "blocked":
                return Blocked(q, occluders, inset);
            case "visible":
                var facet = occluders[q.GetProperty("targetFacet").GetString()!].Facet;
                return GeometryKernel.Facing(Vector(q, "target"), facet.Frame.Normal, Vector(q, "camera")) && !Blocked(q, occluders, inset);
            default:
                throw new InvalidOperationException($"unknown query kind {q.GetProperty("kind")}");
        }
    }

    private static bool Blocked(JsonElement q, IReadOnlyDictionary<string, CoverageOccluder> occluders, double inset)
    {
        var own = q.GetProperty("targetFacet").GetString();
        return occluders.Values.Any(o => o.Facet.Id != own && o.Blocks(Vector(q, "camera"), Vector(q, "target"), inset));
    }

    private static double[] Vector(JsonElement q, string key) => [.. q.GetProperty(key).EnumerateArray().Select(x => x.GetDouble())];

    private static List<double[]> Sorted(IEnumerable<double[]> cuts) =>
        [.. cuts.Select(c => c.Select(x => Math.Round(x, 3)).ToArray()).OrderBy(c => c[0]).ThenBy(c => c[1]).ThenBy(c => c[2])];

    private static string Text(List<double[]> cuts) => string.Join("; ", cuts.Select(c => $"({string.Join(", ", c)})"));

    private static GoldenCase Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var doc = WallGeometryDocument.Parse(root.GetProperty("model").GetRawText());
        var cuts = root.GetProperty("expect").GetProperty("cuts").EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.EnumerateArray().Select(c => c.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToList());
        return new GoldenCase(doc, cuts, [.. root.GetProperty("queries").EnumerateArray().Select(q => q.Clone())]);
    }

    private sealed record GoldenCase(WallGeometryDocument Doc, Dictionary<string, List<double[]>> Cuts, List<JsonElement> Queries)
    {
        public Dictionary<string, CoverageOccluder> Occluders() =>
            CoverageOccluderSeams.Build(CaptureCoverageAnalyzer.Facets(Doc, new Dictionary<string, PlaneRectMm>())).ToDictionary(o => o.Facet.Id);
    }
}
