// <copyright file="AtticGenSevenFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>One proposed new hold of the gen-7 fixture (RAW pixels), with its number in the run's review and its class.</summary>
/// <param name="Index">Its number in the run's new-hold sheets (<c>run/verdicts-new.txt</c>).</param>
/// <param name="Label">realNew, existing (a hold that was never outlined), or the noise class: subPart, marker, nonWall, hangboard, dark.</param>
/// <param name="Candidate">The detection as the triage sees it.</param>
public sealed record GenSevenCandidate(int Index, string Label, TriageCandidate Candidate);

/// <summary>One staged panel of the gen-7 fixture: its proposals, carried holds, markers and the triage input they make.</summary>
public sealed record GenSevenPanel(
    string Name,
    IReadOnlyList<GenSevenCandidate> Candidates,
    NewHoldTriageInput Input,
    IReadOnlyList<PlaneAnchor> Anchors);

/// <summary>
/// Reads <c>Triage/panel-update-attic-gen7.json</c>: The Attic's gen 6 → 7 update of 2026-10-26 on exp (run c7ced54a), whose
/// panel photos were uncropped (4032 × 3024) while gen 6 was cropped. Per panel: the 71 new holds the run proposed, each
/// with the worker's verdict and, for the 41 not-a-hold ones, the class read off <c>diag/noise.jpg</c>; their detections
/// (confidence, radius, outline) re-run on the photos by the app's detector at the same spots and confidences; the carried
/// holds of the gen-7 links but the one moved by hand afterwards (the staged twin, i.e. the re-run detection on the spot,
/// else the promoted hold; old spot; the old hold's facet placement; outlines only near a proposal); the markers
/// the staged photo showed (stored observations plus the triage's lenient pass, run on the photos); and the printed
/// markers of the active model (f058cab2) on its facet planes.
/// </summary>
public static class AtticGenSevenFixture
{
    public static (IReadOnlyDictionary<string, GenSevenPanel> Panels, string ModelJson) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Triage", "panel-update-attic-gen7.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var size = doc.RootElement.GetProperty("size");
        var (width, height) = (size[0].GetInt32(), size[1].GetInt32());
        var panels = doc.RootElement.GetProperty("panels").EnumerateArray()
            .Select(p => ReadPanel(p, width, height))
            .ToDictionary(p => p.Name);
        return (panels, doc.RootElement.GetProperty("model").GetRawText());
    }

    private static GenSevenPanel ReadPanel(JsonElement p, int width, int height)
    {
        var candidates = p.GetProperty("candidates").EnumerateArray().Select(c =>
        {
            var outline = Points(c.GetProperty("outline"));
            var confident = NewHoldTriage.IsConfident(c.GetProperty("conf").GetDouble(), outline is not null, c.GetProperty("oc").GetDouble());
            var candidate = new TriageCandidate(
                Guid.NewGuid(), c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(), c.GetProperty("r").GetDouble(), confident, outline);
            return new GenSevenCandidate(c.GetProperty("i").GetInt32(), c.GetProperty("label").GetString()!, candidate);
        }).ToList();

        // carried: [x, y, r, old x, old y, facet, plane a, plane b, outline]
        var carried = p.GetProperty("carried").EnumerateArray().ToList();
        var outlines = carried.Select(k => new TriageOutline(k[0].GetDouble(), k[1].GetDouble(), k[2].GetDouble(), Points(k[8]))).ToList();
        var anchors = carried
            .Where(k => k[5].ValueKind == JsonValueKind.String && k[6].ValueKind == JsonValueKind.Number && k[7].ValueKind == JsonValueKind.Number)
            .Select(k => new PlaneAnchor(k[0].GetDouble() / width, k[1].GetDouble() / height, k[5].GetString()!, k[6].GetDouble(), k[7].GetDouble()))
            .ToList();
        var input = new NewHoldTriageInput(
            candidates.Select(c => c.Candidate).ToList(),
            carried.Select(k => new PointPair(k[0].GetDouble(), k[1].GetDouble(), k[3].GetDouble(), k[4].GetDouble())).ToList(),
            (p.GetProperty("oldSize")[0].GetInt32(), p.GetProperty("oldSize")[1].GetInt32()),
            p.GetProperty("markers").EnumerateArray().Select(q => (IReadOnlyList<(double X, double Y)>)Points(q)!).ToList(),
            Carried: outlines.Select(o => new TriageSpot(o.X, o.Y, o.RadiusPx)).ToList(),
            CarriedOutlines: outlines);
        return new GenSevenPanel(p.GetProperty("name").GetString()!, candidates, input, anchors);
    }

    private static List<(double X, double Y)>? Points(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(v => (v[0].GetDouble(), v[1].GetDouble())).ToList() : null;
}
