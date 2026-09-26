// <copyright file="MarkerlessFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Markerless captures in tests: workers that offer <c>solve-sfm</c> and a splat-prepare with sparse.zip + anchors, a
/// detector whose markers can be switched off between captures, and geometry documents. The wall's main facet "0" is the
/// plane y = 0 (normal −y, towards the climber), 3000 × 2500 mm; cameras stand 3 m in front of it, looking at it.
/// </summary>
internal static class MarkerlessFixture
{
    public static readonly string[] SplitKinds = ["splat", WallCaptureProcessor.PrepareKind, WallCaptureProcessor.FinishKind];

    /// <summary>A capture scenario whose workers can run markerless captures (unless <paramref name="sfm"/> is false).</summary>
    public static CaptureScenario Scenario(
        WallTestHarness h, SwitchableMarkerDetector detector, bool sfm = true, GpuRunnerOptions? runners = null, MutableTestClock? clock = null,
        IHoldDetectionService? holds = null)
    {
        var s = new CaptureScenario(h, detector, runnerOptions: runners, clock: clock, holdDetection: holds);
        s.Client.Kinds = sfm ? ["solve", MarkerlessCaptureSupport.SolveKind, "textures"] : ["solve", "textures"];
        s.SplatClient.IsConfigured = true;
        s.SplatClient.Kinds = [.. SplitKinds];
        s.SplatClient.PrepareOutputs = sfm ? [MarkerlessCaptureSupport.SparseOutput, MarkerlessCaptureSupport.AnchorsOutput] : [];
        s.SplatClient.Download = name => name switch
        {
            "bundle.zip" => RunnerFixture.Bundle(),
            "prepared.json" => "{\"version\":2,\"photoCentres\":{}}"u8.ToArray(),
            "sparse.zip" => SparseFixture.Zip(SparseFixture.Surface((_, _) => 0, stepMm: 250)),
            "frame.json" => System.Text.Encoding.UTF8.GetBytes(s.SplatClient.FrameJson),
            "wall.spz" => s.SplatClient.Spz,
            _ => CaptureScenario.TinyJpeg(),
        };
        return s;
    }

    /// <summary>Opens a draft on the scenario's wall, uploads photos and starts it without declarations.</summary>
    public static async Task<Guid> StartAsync(CaptureScenario s, int photos = 3, bool seedWall = true)
    {
        if (seedWall)
        {
            await s.Harness.SeedWallAsync(holdCount: 0);
        }

        var draft = await s.Service.CreateDraftAsync(s.Harness.WallId);
        for (var i = 0; i < photos; i++)
        {
            await s.Service.AddPhotoAsync(draft.CaptureId, $"IMG_{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 3 + i)), CancellationToken.None);
        }

        var problems = await s.Service.StartAsync(draft.CaptureId, new CaptureDeclarations([], []), "no markers");
        Assert.Empty(problems);
        return draft.CaptureId;
    }

    /// <summary>A feature model (<c>solve-sfm</c>): facet "0" on the main wall plane and facet "1", a side surface at x = −500.</summary>
    public static string FeatureDoc(bool anchored, string? anchorReason = null, bool gravityKnown = true, params string[] cameras)
    {
        var world = new JsonObject
        {
            ["origin"] = "test", ["up"] = new JsonArray(0.0, 0.0, 1.0), ["gravityKnown"] = gravityKnown, ["referenceFacet"] = "0",
            ["frameSource"] = "features", ["gravitySource"] = anchored ? "anchors" : "cameras",
            ["scaleKnown"] = anchored, ["scaleSource"] = anchored ? "anchors" : "estimate", ["anchored"] = anchored,
        };
        var anchors = anchorReason is null ? (JsonNode?)null : new JsonObject { ["ok"] = false, ["reason"] = anchorReason };
        return new JsonObject
        {
            ["version"] = 1, ["units"] = "mm", ["dictionary"] = "DICT_4X4_50", ["idScheme"] = "plan", ["markerSizeMm"] = 125.0,
            ["world"] = world,
            ["segments"] = new JsonArray(
                Segment(0, "Main surface", Facet("0", [0, 0, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0], 3000, 2500, 44.9)),
                Segment(1, "Surface 1", Facet("1", [-500, -1500, 0], [0, 1, 0], [0, 0, 1], [1, 0, 0], 1500, 2500, 0))),
            ["markers"] = new JsonArray(),
            ["cameras"] = Cameras(cameras.Length == 0 ? ["p01", "p02", "p03"] : cameras),
            ["quality"] = new JsonObject { ["reprojRmsPx"] = 0.8, ["sfm"] = new JsonObject { ["anchors"] = anchors } },
        }.ToJsonString();
    }

    /// <summary>A marker model with posed cameras: facet "0" (same plane as the feature doc's) and facet "5" on the right.</summary>
    public static string MarkerDoc(params string[] cameras) => new JsonObject
    {
        ["version"] = 1, ["units"] = "mm", ["dictionary"] = "DICT_4X4_50", ["markerSizeMm"] = 125.0,
        ["world"] = new JsonObject { ["up"] = new JsonArray(0.0, 0.0, 1.0), ["gravityKnown"] = true },
        ["segments"] = new JsonArray(
            Segment(0, "main wall", Facet("0", [0, 0, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0], 3000, 2500, 45)),
            Segment(5, "right piece", Facet("5", [3000, 0, 0], [0, -1, 0], [0, 0, 1], [-1, 0, 0], 1000, 2500, 0))),
        ["markers"] = new JsonArray(Marker(0, "0", 200), Marker(1, "0", 1800), Marker(2, "5", 300)),
        ["cameras"] = Cameras(cameras.Length == 0 ? ["p01", "p02", "p03"] : cameras),
        ["quality"] = new JsonObject { ["reprojRmsPx"] = 0.7 },
    }.ToJsonString();

    /// <summary>A camera 3 m in front of the wall at x, looking straight at it (x right, y down = −z, z forward = +y).</summary>
    public static JsonObject Camera(string image, double x, double z = 1250) => new()
    {
        ["image"] = image, ["width"] = 2000, ["height"] = 1500,
        ["K"] = new JsonArray(1000.0, 0.0, 1000.0, 0.0, 1000.0, 750.0, 0.0, 0.0, 1.0),
        ["dist"] = new JsonArray(0.0, 0.0, 0.0, 0.0, 0.0),
        ["R"] = new JsonArray(1.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, 1.0, 0.0),
        ["t"] = new JsonArray(-x, z, 3000.0),
    };

    private static JsonArray Cameras(string[] images) =>
        new(images.Select((image, i) => (JsonNode?)Camera(image, 500 + (1000 * (i % 3)), 800 + (400 * (i / 3)))).ToArray());

    private static JsonObject Segment(int index, string name, JsonObject facet) => new()
    {
        ["index"] = index, ["name"] = name, ["measuredAngleDeg"] = facet["measuredAngleDeg"]?.DeepClone(), ["facets"] = new JsonArray(facet),
    };

    private static JsonObject Facet(string id, double[] origin, double[] u, double[] v, double[] n, double width, double height, double angle) => new()
    {
        ["id"] = id,
        ["origin"] = new JsonArray(origin.Select(x => (JsonNode?)x).ToArray()),
        ["u"] = new JsonArray(u.Select(x => (JsonNode?)x).ToArray()),
        ["v"] = new JsonArray(v.Select(x => (JsonNode?)x).ToArray()),
        ["normal"] = new JsonArray(n.Select(x => (JsonNode?)x).ToArray()),
        ["measuredAngleDeg"] = angle,
        ["extentMm"] = new JsonObject { ["aMin"] = 0.0, ["aMax"] = width, ["bMin"] = 0.0, ["bMax"] = height },
    };

    private static JsonObject Marker(int id, string facet, double a) => new()
    {
        ["id"] = id, ["segment"] = facet == "5" ? 5 : 0, ["facet"] = facet, ["observations"] = 3,
        ["cornersPlaneMm"] = new JsonArray(
            new JsonArray(a, 1125.0), new JsonArray(a + 125, 1125.0), new JsonArray(a + 125, 1000.0), new JsonArray(a, 1000.0)),
    };
}

/// <summary>A marker detector whose markers can be switched off (a later capture of the same wall without markers).</summary>
internal sealed class SwitchableMarkerDetector : IMarkerDetectionService
{
    public int[] Ids { get; set; } = [0, 1, 2, 6, 7, 12];

    public Task<MarkerDetectionResult> DetectAsync(byte[] image, MarkerDetectionOptions? options, CancellationToken ct) =>
        new FakeMarkerDetectionService(Ids).DetectAsync(image, options, ct);
}
