// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// Reads the result documents of the <c>markers</c>, <c>presence</c>, <c>match-panels</c>, <c>outline-holds</c> and
/// <c>texture-match</c> kinds (contracts
/// <c>jobs/recognition/*-result</c>) back into the in-process types. A document off the contract throws, and
/// <see cref="RecognitionCaller"/> treats the job as not served.
/// </summary>
public static class RecognitionResults
{
    public static MarkerDetectionResult Markers(JsonElement r) => new()
    {
        ImageWidth = r.GetProperty("imageWidth").GetInt32(),
        ImageHeight = r.GetProperty("imageHeight").GetInt32(),
        Markers = Items(r, "markers").Select(m => new DetectedMarker
        {
            Id = m.GetProperty("id").GetInt32(),
            CornersPx = MarkerPoints(m.GetProperty("cornersPx")),
            CornersNormalized = MarkerPoints(m.GetProperty("cornersNormalized")),
            SidePx = m.GetProperty("sidePx").GetDouble(),
            EdgeRatio = m.GetProperty("edgeRatio").GetDouble(),
            Synthetic = m.TryGetProperty("synthetic", out var s) && s.GetBoolean(),
        }).ToList(),
        Rejected = Items(r, "rejected").Select(c => new RejectedMarkerCandidate(
            c.GetProperty("id").GetInt32(),
            MarkerPoints(c.GetProperty("cornersPx")),
            c.GetProperty("sidePx").GetDouble(),
            c.GetProperty("edgeRatio").GetDouble(),
            Enum.Parse<MarkerRejectionReason>(c.GetProperty("reason").GetString() ?? string.Empty),
            c.TryGetProperty("detail", out var d) ? d.GetString() ?? string.Empty : string.Empty)).ToList(),
        UndecodedCandidateCount = r.TryGetProperty("undecodedCandidateCount", out var u) ? u.GetInt32() : 0,
        Suspicious = r.TryGetProperty("suspicious", out var sus) && sus.GetBoolean(),
        Warnings = (Optional(r, "warnings") ?? []).Select(w => w.GetString() ?? string.Empty).ToList(),
    };

    public static IReadOnlyList<double?> Presence(JsonElement r) =>
        Items(r, "scores").Select(s => s.ValueKind == JsonValueKind.Null ? (double?)null : s.GetDouble()).ToList();

    public static HoldOverlapResult Match(JsonElement r) => new(
        Items(r, "proposals").Select(p => new HoldOverlapProposal(
            p.GetProperty("leftHoldId").GetInt32(),
            p.GetProperty("rightHoldId").GetInt32(),
            p.GetProperty("confidence").GetDouble(),
            p.GetProperty("moved").GetBoolean(),
            p.GetProperty("residualPx").GetDouble(),
            p.TryGetProperty("rescue", out var rescue) ? rescue.GetString() : null)).ToList(),
        Items(r, "unmatchedLeft").Select(i => i.GetInt32()).ToList(),
        Items(r, "unmatchedRight").Select(i => i.GetInt32()).ToList(),
        Optional(r, "warpedLeftPositions")?.Select(p => p.ValueKind == JsonValueKind.Null ? ((double X, double Y)?)null : Point(p)).ToList(),
        Optional(r, "warpedLeftShapes")?.Select(s => s.ValueKind == JsonValueKind.Null
            ? null
            : (IReadOnlyList<(double X, double Y)>)s.EnumerateArray().Select(Point).ToList()).ToList());

    /// <summary>
    /// The outlines of <paramref name="count"/> seeds sent with ids 0..count-1, on a photo of the given size. A seed the runner
    /// could not outline makes the whole result unreadable: the in-process outliner always answers, with a circle at worst.
    /// </summary>
    public static HoldOutlineBatchResult Outlines(JsonElement r, int width, int height, int count)
    {
        var outlines = Items(r, "outlines").Select(Outline).ToList();
        return outlines.Count == count && outlines.Select(o => o.Id).SequenceEqual(Enumerable.Range(0, count))
            ? new HoldOutlineBatchResult(width, height, outlines.Select(o => o.Outline).ToList())
            : throw new InvalidDataException("The outlines do not answer the seeds in order.");
    }

    public static PhotoTextureMatch TextureMatch(JsonElement r) => new(
        Items(r, "pairs").Select(p => p.Deserialize<PointCorrespondence>(RecognitionParts.Json)).ToList(),
        r.GetProperty("coarseInliers").GetInt32(),
        r.TryGetProperty("failure", out var f) ? f.GetString() : null);

    private static (int Id, HoldOutlineResult Outline) Outline(JsonElement o)
    {
        var id = o.GetProperty("id").GetInt32();
        if (o.TryGetProperty("failure", out var failure))
        {
            throw new InvalidDataException($"Seed {id} has no outline: {failure.GetString()}");
        }

        var outline = new HoldOutlineResult(
            o.GetProperty("polygon").EnumerateArray().Select(p => new NormalizedPoint(p[0].GetDouble(), p[1].GetDouble())).ToList(),
            o.GetProperty("anchorX").GetDouble(),
            o.GetProperty("anchorY").GetDouble(),
            Read<List<ShapePoint>>(o, "shapePoints"),
            o.GetProperty("areaPx").GetDouble(),
            o.GetProperty("bounds").Deserialize<HoldOutlineBounds>(RecognitionParts.Json),
            o.GetProperty("confidence").GetDouble(),
            Enum.Parse<HoldOutlineMethod>(o.GetProperty("method").GetString() ?? string.Empty),
            o.GetProperty("fingerprint").Deserialize<HoldFingerprint>(RecognitionParts.Json) ?? throw new InvalidDataException($"Seed {id} has no fingerprint."),
            Read<List<List<ShapePoint>>>(o, "shapeHoles"))
        {
            Corroborated = Flag(o, "corroborated"),
            Distinct = Flag(o, "distinct"),
            Pale = Flag(o, "pale"),
        };
        return (id, outline);
    }

    private static T? Read<T>(JsonElement o, string name)
        where T : class =>
        o.TryGetProperty(name, out var value) ? value.Deserialize<T>(RecognitionParts.Json) : null;

    private static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var value) && value.GetBoolean();

    private static IEnumerable<JsonElement> Items(JsonElement r, string name) =>
        Optional(r, name) ?? throw new InvalidDataException($"The result has no '{name}'.");

    private static IEnumerable<JsonElement>? Optional(JsonElement r, string name) =>
        r.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : null;

    private static (double X, double Y) Point(JsonElement p) => (p[0].GetDouble(), p[1].GetDouble());

    private static List<MarkerPoint> MarkerPoints(JsonElement points) =>
        points.EnumerateArray().Select(p => new MarkerPoint(p[0].GetDouble(), p[1].GetDouble())).ToList();
}
