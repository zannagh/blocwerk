using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.HoldDetection.Tests.Outlines;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// Read-only dry run of re-placing the holds after a re-capture, the way the placement service does it: each panel
/// photo registered with its holds' previous placements as anchors, a photo that still leaves holds unplaced
/// registered again with its linked holds as further anchors, and whatever still fails carried over.
/// <c>BLOCWERK_REPLACE_DIR</c> is an export as for <see cref="AtticTextureRegistrationMeasurement"/> (the new
/// model's textures and <c>model.json</c>, holds with their current placements) plus <c>previous-model.json</c>
/// (the model the holds were placed on) and <c>links.json</c> (<c>HoldAId, HoldBId</c> pairs). Skipped otherwise.
/// </summary>
public class AtticReplacementMeasurement(ITestOutputHelper output)
{
    [SkippableFact]
    public void DryRun_ReplacesTheHoldsOnARecapturedModel()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_REPLACE_DIR");
        Skip.If(string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "previous-model.json")), "Set BLOCWERK_REPLACE_DIR to a re-placement export.");
        var generation = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_GENERATION"), out var g) ? g : 3;
        var textures = AtticTextureRegistrationMeasurement.LoadTextures(dir!, Path.Combine(dir!, "textures", "textures.json"));
        var active = WallGeometryDocument.Parse(File.ReadAllText(Path.Combine(dir!, "model.json")));
        var previous = Frames(WallGeometryDocument.Parse(File.ReadAllText(Path.Combine(dir!, "previous-model.json"))));
        var (frames, extents) = (Frames(active), Wall3DFallbackPlacement.FacetExtents(active));
        var rows = JsonSerializer.Deserialize<List<AtticExportedHold>>(File.ReadAllText(Path.Combine(dir!, "holds.json")))!;
        var holds = rows.Where(r => r.Generation <= generation && r.WallPanelId is not null).Select(r => r.ToHold()).ToList();
        var carried = holds
            .Where(h => h.FacetId is { } f && previous.ContainsKey(f) && frames.ContainsKey(f) && extents.ContainsKey(f) && h.PlaneAMm is not null)
            .Select(h => (h.Id, P: PlacementCarrier.Carry(previous[h.FacetId!], frames[h.FacetId!], extents[h.FacetId!], h.PlaneAMm!.Value, h.PlaneBMm!.Value)))
            .Where(x => x.P is not null)
            .ToDictionary(x => x.Id, x => new PlaneAnchor(0, 0, holds.First(h => h.Id == x.Id).FacetId!, x.P!.Value.A, x.P!.Value.B));
        var links = Links(Path.Combine(dir!, "links.json"));
        var panels = holds.GroupBy(h => h.WallPanelId!.Value).ToDictionary(p => p.Key, p => p.ToList());
        output.WriteLine($"{holds.Count} holds, {carried.Count} with a previous placement carried onto the new model, {links.Count} links");

        var placed = new Dictionary<Guid, Dictionary<Guid, HoldPlaneFit>>();
        foreach (var (panel, list) in panels)
        {
            placed[panel] = Run(dir!, generation, rows, panel, list, textures, Own(list, carried), "own anchors");
        }

        foreach (var (panel, list) in panels)
        {
            var elsewhere = placed.Where(p => p.Key != panel).SelectMany(p => p.Value).ToDictionary(p => p.Key, p => p.Value);
            var linked = list.SelectMany(h => links[h.Id].Where(elsewhere.ContainsKey)
                .Select(o => new PlaneAnchor(h.X, h.Y, elsewhere[o].FacetId, elsewhere[o].PlaneAMm, elsewhere[o].PlaneBMm))).ToList();
            output.WriteLine($"{Label(rows, panel)}: {linked.Count} linked holds placed from the other photos");
            if (linked.Count >= AnchorSeed.MinAnchors)
            {
                Run(dir!, generation, rows, panel, list, textures, linked, "linked holds only (no previous placements)");
            }

            var moved = placed[panel].Where(p => carried.TryGetValue(p.Key, out var c) && c.FacetId == p.Value.FacetId)
                .Select(p => Math.Sqrt(Math.Pow(p.Value.PlaneAMm - carried[p.Key].A, 2) + Math.Pow(p.Value.PlaneBMm - carried[p.Key].B, 2)))
                .Order().ToList();
            if (moved.Count > 0)
            {
                output.WriteLine($"{Label(rows, panel)}: registered vs previous placement, median {moved[moved.Count / 2]:F1} mm, "
                    + $"p95 {moved[(int)(0.95 * (moved.Count - 1))]:F1} mm over {moved.Count} holds");
            }

            var kept = list.Count(h => !placed[panel].ContainsKey(h.Id) && carried.ContainsKey(h.Id));
            output.WriteLine($"{Label(rows, panel)} with the fix: {placed[panel].Count} registered + {kept} carried = "
                + $"{placed[panel].Count + kept} of {list.Count}");
        }
    }

    private Dictionary<Guid, HoldPlaneFit> Run(
        string dir, int generation, List<AtticExportedHold> rows, Guid panel, List<Hold> holds, List<RegistrationTexture> textures, List<PlaneAnchor> anchors, string how)
    {
        var label = Label(rows, panel);
        var photo = File.ReadAllBytes(Path.Combine(dir, "photos", $"panel_{label}_r0_g{generation}.jpg"));
        using var session = new OpenCvPhotoTextureMatcher().OpenPhoto(photo);
        var focal = ExifCameraReader.Read(photo).Focal35mm is { } f35 ? f35 / 36.0 * Math.Max(session.Width, session.Height) : (double?)null;
        output.WriteLine($"--- {label}, {how}: {anchors.Count} anchors");
        var registrations = new PhotoRegistrar(session, new TestOutputLogger(output), label, focal, anchors).RegisterAll(textures);
        var fits = holds.Select(h => (h.Id, Fit: HoldTexturePlacer.Place(h, registrations))).Where(x => x.Fit is not null).ToDictionary(x => x.Id, x => x.Fit!);
        output.WriteLine($"{label}, {how}: {fits.Count} of {holds.Count} registered: "
            + string.Join(", ", fits.Values.GroupBy(x => x.FacetId).OrderBy(x => x.Key).Select(x => $"facet {x.Key}: {x.Count()}")));
        return fits;
    }

    private static List<PlaneAnchor> Own(List<Hold> holds, Dictionary<Guid, PlaneAnchor> carried) =>
        holds.Where(h => carried.ContainsKey(h.Id)).Select(h => carried[h.Id] with { X = h.X, Y = h.Y }).ToList();

    private static string Label(List<AtticExportedHold> rows, Guid panel) => $"c{rows.First(r => r.WallPanelId == panel).PanelCol}";

    private static Dictionary<string, FacetFrame> Frames(WallGeometryDocument doc) =>
        doc.Segments.SelectMany(s => s.Facets).Where(f => f.Id is not null && FacetFrame.From(f) is not null)
            .ToDictionary(f => f.Id!, f => FacetFrame.From(f)!);

    private static ILookup<Guid, Guid> Links(string path)
    {
        var pairs = JsonSerializer.Deserialize<List<AtticExportedLink>>(File.ReadAllText(path)) ?? [];
        return pairs.SelectMany(l => new[] { (l.HoldAId, l.HoldBId), (l.HoldBId, l.HoldAId) }).ToLookup(x => x.Item1, x => x.Item2);
    }
}
