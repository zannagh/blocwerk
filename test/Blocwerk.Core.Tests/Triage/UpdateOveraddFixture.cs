// <copyright file="UpdateOveraddFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>One unpaired staged detection of the over-add fixture (RAW pixels), with the verifier's label and the run's verdict.</summary>
/// <param name="Id">The staged hold.</param>
/// <param name="X">Centre x.</param>
/// <param name="Y">Centre y.</param>
/// <param name="RadiusPx">Radius.</param>
/// <param name="Outline">Its outline, or null.</param>
/// <param name="Confidence">The detector's confidence.</param>
/// <param name="OutlineConfidence">The outliner's confidence, or null.</param>
/// <param name="Before">The discard reason the real run gave it, or null when it became a new hold.</param>
/// <param name="Label">The verifier's class (verify.json), or realDiscarded / checkedNotNew / unchecked / unlabelled.</param>
/// <param name="Presence">The presence probe's score against the old photo, measured on the photos.</param>
/// <param name="Lab">Its mean colour from its fingerprint, or null.</param>
/// <param name="PresenceOwn">The presence score asked with the detection's own radius (as the swap check asks it).</param>
public sealed record OveraddCandidate(
    Guid Id,
    double X,
    double Y,
    double RadiusPx,
    IReadOnlyList<(double X, double Y)>? Outline,
    double Confidence,
    double? OutlineConfidence,
    string? Before,
    string Label,
    double? Presence,
    TriageLab? Lab,
    double? PresenceOwn);

/// <summary>One staged panel of the over-add fixture.</summary>
public sealed record OveraddPanel(
    string Name,
    (int Width, int Height) OldSize,
    IReadOnlyList<OveraddCandidate> Candidates,
    IReadOnlyList<(TriageOutline Outline, bool Kept, bool Gone)> Carriers,
    IReadOnlyList<PointPair> Pairs,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> Markers,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> OldMarkers,
    IReadOnlyList<TriageSpot> Settled,
    IReadOnlyList<(PointPair Pair, double Confidence)> OverlapToMain,
    TriageLab? ColourShift);

/// <summary>One of the 40 pairs the real run reported as moved, with the verifier's verdict.</summary>
/// <param name="M">The pair's number in the verifier's report.</param>
/// <param name="Label">reOutline, jump or hiddenChange (none really moved).</param>
/// <param name="Boulders">How many live boulders use the hold.</param>
/// <param name="Old">The old footprint where the other pairs' local warp puts it on the new photo.</param>
/// <param name="Twin">The detection it was carried onto.</param>
/// <param name="DeclinedByMatcher">Whether the matcher's uniqueness margin now declines the pair (measured on the photos).</param>
/// <param name="Mm">The distance the real run measured, mm.</param>
/// <param name="RotationDeg">The turn the real run measured, when known.</param>
public sealed record OveraddMove(
    int M, string Label, int Boulders, TriageOutline Old, TriageOutline Twin, bool DeclinedByMatcher, double Mm, double? RotationDeg);

/// <summary>
/// Reads <c>Triage/update-overadd-attic.json</c>: The Attic's quick update of 2026-10-19 (gen 5 → 6) as staged, before the
/// promote (<c>blocwerk_midoct_preapply</c>), geometry only. Per panel: every unpaired staged detection with the run's own
/// verdict and the verifier's label (<c>.me/mid-oct-update/verify/verify.json</c>), the footprints of the carried holds
/// near them (refound twins, and the kept old holds warped by the local fit of the twins), the staged → old pairs, the
/// marker observations of both photos; for the right panel the overlap pairs onto the centre photo. The presence scores,
/// the overlap pairs and the pairs the matcher's uniqueness margin declines were measured on the photos
/// (<c>UpdateOveraddMeasurement</c>). Both photos are 4032 × 3024.
/// </summary>
public static class UpdateOveraddFixture
{
    public static (IReadOnlyDictionary<string, OveraddPanel> Panels, IReadOnlyList<OveraddMove> Moves) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Triage", "update-overadd-attic.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var panels = doc.RootElement.GetProperty("panels").EnumerateArray().Select(ReadPanel).ToDictionary(p => p.Name);
        var moves = doc.RootElement.GetProperty("moves").EnumerateArray().Select(m => new OveraddMove(
            m.GetProperty("m").GetInt32(),
            m.GetProperty("label").GetString()!,
            m.GetProperty("boulders").GetInt32(),
            Footprint(m.GetProperty("old")),
            Footprint(m.GetProperty("twin")),
            m.GetProperty("declinedByMatcher").GetBoolean(),
            m.GetProperty("mm").GetDouble(),
            Number(m.GetProperty("rot")))).ToList();
        return (panels, moves);
    }

    private static OveraddPanel ReadPanel(JsonElement p)
    {
        var candidates = p.GetProperty("candidates").EnumerateArray().Select(c => new OveraddCandidate(
            Guid.Parse(c.GetProperty("id").GetString()!),
            c.GetProperty("x").GetDouble(),
            c.GetProperty("y").GetDouble(),
            c.GetProperty("r").GetDouble(),
            Points(c.GetProperty("outline")),
            c.GetProperty("conf").GetDouble(),
            Number(c.GetProperty("oc")),
            c.GetProperty("before").GetString(),
            c.GetProperty("label").GetString()!,
            Number(c.GetProperty("presence")),
            Lab(c),
            Number(c.GetProperty("presenceOwn")))).ToList();
        var carriers = p.GetProperty("carriers").EnumerateArray()
            .Select(k => (Footprint(k), k.GetProperty("kind").GetString() == "kept", k.TryGetProperty("gone", out var g) && g.GetBoolean())).ToList();
        var settled = p.TryGetProperty("settled", out var s)
            ? s.EnumerateArray().Select(v => new TriageSpot(v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble())).ToList()
            : [];
        var overlap = p.TryGetProperty("overlapToMain", out var o)
            ? o.EnumerateArray().Select(q => (Pair(q), q[4].GetDouble())).ToList()
            : [];
        return new OveraddPanel(
            p.GetProperty("name").GetString()!,
            (p.GetProperty("oldSize")[0].GetInt32(), p.GetProperty("oldSize")[1].GetInt32()),
            candidates,
            carriers,
            p.GetProperty("pairs").EnumerateArray().Select(Pair).ToList(),
            Quads(p.GetProperty("markers")),
            Quads(p.GetProperty("oldMarkers")),
            settled,
            overlap,
            p.TryGetProperty("colourShift", out var w) ? new TriageLab(w[0].GetDouble(), w[1].GetDouble(), w[2].GetDouble()) : null);
    }

    private static TriageOutline Footprint(JsonElement e) =>
        new(e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("r").GetDouble(), Points(e.GetProperty("outline")), Lab(e));

    private static TriageLab? Lab(JsonElement e) =>
        e.TryGetProperty("lab", out var l) && l.ValueKind == JsonValueKind.Array ? new TriageLab(l[0].GetDouble(), l[1].GetDouble(), l[2].GetDouble()) : null;

    private static PointPair Pair(JsonElement q) => new(q[0].GetDouble(), q[1].GetDouble(), q[2].GetDouble(), q[3].GetDouble());

    private static List<(double X, double Y)>? Points(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(v => (v[0].GetDouble(), v[1].GetDouble())).ToList() : null;

    private static List<IReadOnlyList<(double X, double Y)>> Quads(JsonElement e) =>
        e.EnumerateArray().Select(q => (IReadOnlyList<(double X, double Y)>)Points(q)!).ToList();

    private static double? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
}
