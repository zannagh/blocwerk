// <copyright file="AtticTriage3DMeasurement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Blocwerk.HoldDetection.Matching;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// Read-only measurement of the panel update's 3D evidence on The Attic (never committed data): <c>BLOCWERK_ATTIC_DIR</c>
/// is a placement export of the model built from the same visit (<c>textures/</c>, <c>model.json</c>, <c>holds.json</c>
/// with the updated generation's holds and their placements, <c>proposals.json</c>, the panel photos of that generation)
/// plus <c>panel-update-attic.json</c>, the labelled triage fixture. The old holds' placements are simulated by the
/// updated generation's holds minus the real new ones. Prints phase 1's triage against phase 1 + 3D. Skipped otherwise.
/// </summary>
public class AtticTriage3DMeasurement(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [SkippableFact]
    public void Attic_3DEvidence_AgainstThePhotoOnlyTriage()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_DIR");
        var fixturePath = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "panel-update-attic.json");
        Skip.If(fixturePath is null || !File.Exists(fixturePath), "Set BLOCWERK_ATTIC_DIR to an export with panel-update-attic.json.");
        var generation = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_GENERATION"), out var g) ? g : 4;
        var textures = AtticTextureRegistrationMeasurement.LoadTextures(dir!, Path.Combine(dir!, "textures", "textures.json"));
        var holds = JsonSerializer.Deserialize<List<ExportedHold>>(File.ReadAllText(Path.Combine(dir!, "holds.json")), Json)!
            .Where(h => h.Generation == generation).ToList();
        var proposals = JsonSerializer.Deserialize<List<ExportedProposal>>(File.ReadAllText(Path.Combine(dir!, "proposals.json")), Json)!;
        var fixture = AtticFixturePanels.Load(fixturePath!);
        var added = Added(holds, fixture);
        var paired = Paired(holds, fixture);
        var facets = Facets(dir!);

        var main = Measure("main", 0, fixture["main"], (null, null), holds, added, paired, facets, proposals, textures, generation, dir!);
        var right = fixture["right"];
        // Each right-panel row sees the centre as that same row kept it: phase 1 alone with phase 1's centre.
        var owners = (
            new OverlapOwner(right.OverlapToMain, fixture["main"].Size, main.PhotoOnly, right.Size),
            new OverlapOwner(right.OverlapToMain, fixture["main"].Size, main.With3D, right.Size));
        Measure("right", 1, right, owners, holds, added, paired, facets, proposals, textures, generation, dir!);
    }

    private (List<(double X, double Y)> PhotoOnly, List<(double X, double Y)> With3D) Measure(
        string name, int col, AtticFixturePanel panel, (OverlapOwner? PhotoOnly, OverlapOwner? With3D) owner, List<ExportedHold> holds, List<ExportedHold> added,
        List<ExportedHold> paired, Dictionary<string, ModelFacet> facets, List<ExportedProposal> proposals, List<RegistrationTexture> textures, int generation, string dir)
    {
        var (w, h) = panel.Size;
        // As the service: only old holds the matcher found again (a twin at one of the fixture's staged → old pairs).
        var known = paired
            .Where(x => x is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null, IsVolume: false }
                && x.MetricSource != "texture-registration-rejected" && !IsNew(x, added))
            .Select(x => new FacetSpot(x.FacetId!, x.PlaneAMm!.Value, x.PlaneBMm!.Value, Math.Max(x.WidthMm ?? 0, x.HeightMm ?? 0) / 2))
            .ToList();
        var seen = proposals.Select(p => new FacetSpot(p.FacetId, p.A, p.B, p.SizeMm / 2)).ToList();

        var photo = File.ReadAllBytes(Path.Combine(dir, "photos", $"panel_c{col}_r0_g{generation}.jpg"));
        using var session = new OpenCvPhotoTextureMatcher().OpenPhoto(photo);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // As the service seeds it: the matched old holds of this panel at their placements (off with BLOCWERK_ATTIC_NO_ANCHORS).
        var anchors = Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_NO_ANCHORS") is not null
            ? []
            : holds.Where(x => x.PanelCol == col && x is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null } && !IsNew(x, added))
                .Select(x => new PlaneAnchor(x.X, x.Y, x.FacetId!, x.PlaneAMm!.Value, x.PlaneBMm!.Value)).ToList();
        var registrations = new PhotoRegistrar(session, new TestOutputLogger(output), $"c{col}", anchors: anchors).RegisterAll(textures);
        output.WriteLine($"{name}: registered to {registrations.Count(r => r.Accepted)}/{registrations.Count} facets in {watch.ElapsedMilliseconds} ms; "
            + $"{known.Count} known placements, {seen.Count} proposals");

        var focal = ExifCameraReader.Read(photo).Focal35mm is { } f35 ? f35 / 36.0 * Math.Max(session.Width, session.Height) : (double?)null;
        var evidence = new Panel3DEvidence(registrations, known, seen, facets, session.Width, session.Height, focal);
        var verdicts = panel.Candidates.ToDictionary(c => c.Id, c => NewHoldEvidence3D.Judge(evidence, c.X / w, c.Y / h));
        Report(name, "3D alone", panel, verdicts.Where(v => v.Value is Evidence3DVerdict.KnownHold or Evidence3DVerdict.OffWall)
            .ToDictionary(v => v.Key, v => v.Value.ToString()));
        foreach (var c in panel.Candidates.Where(c => c.Real))
        {
            output.WriteLine($"  real new hold at ({c.X:0}, {c.Y:0}): {verdicts[c.Id]}");
        }

        foreach (var verdict in Enum.GetValues<Evidence3DVerdict>())
        {
            var ids = verdicts.Where(v => v.Value == verdict).Select(v => v.Key).ToHashSet();
            output.WriteLine($"  {verdict}: {panel.Candidates.Count(c => c.Real && ids.Contains(c.Id))} real, {panel.Candidates.Count(c => !c.Real && ids.Contains(c.Id))} junk");
        }

        CatchSweep(panel, registrations, known);
        var photoOnly = Classify(panel, owner.PhotoOnly, null);
        var with3D = Classify(panel, owner.With3D, verdicts);
        Assert.True(photoOnly.Keys.All(with3D.ContainsKey), $"{name}: 3D evidence must only add discards");
        Report(name, "phase 1", panel, photoOnly.ToDictionary(r => r.Key, r => r.Value.ToString()));
        Report(name, "phase 1 + 3D", panel, with3D.ToDictionary(r => r.Key, r => r.Value.ToString()));
        return (Kept(panel, photoOnly), Kept(panel, with3D));
    }

    private static List<(double X, double Y)> Kept(AtticFixturePanel panel, Dictionary<Guid, NewHoldDiscardReason> discarded) =>
        panel.Candidates.Where(c => !discarded.ContainsKey(c.Id)).Select(c => (c.X, c.Y)).ToList();


    /// <summary>The holds the matcher paired: at the staged end of one of their panel's staged → old pairs.</summary>
    private static List<ExportedHold> Paired(List<ExportedHold> holds, IReadOnlyDictionary<string, AtticFixturePanel> fixture) =>
        holds.Where(x => x.PanelCol is { } col && fixture[col == 0 ? "main" : "right"] is var p
            && p.Pairs.Any(q => Math.Abs((x.X * p.Size.Width) - q.SrcX) < 4 && Math.Abs((x.Y * p.Size.Height) - q.SrcY) < 4)).ToList();

    private static Dictionary<string, ModelFacet> Facets(string dir)
    {
        var doc = WallGeometryDocument.Parse(File.ReadAllText(Path.Combine(dir, "model.json")));
        var extents = Wall3DFallbackPlacement.FacetExtents(doc);
        return doc.Segments.SelectMany(s => s.Facets)
            .Where(f => f.Id is not null && extents.ContainsKey(f.Id) && FacetFrame.From(f) is not null)
            .GroupBy(f => f.Id!)
            .ToDictionary(g => g.Key, g => new ModelFacet(FacetFrame.From(g.First())!, extents[g.Key]));
    }

    /// <summary>The holds the update added: at a real candidate of their own panel.</summary>
    private static List<ExportedHold> Added(List<ExportedHold> holds, IReadOnlyDictionary<string, AtticFixturePanel> fixture) =>
        holds.Where(x => x.PanelCol is { } col && fixture[col == 0 ? "main" : "right"] is var p
            && p.Candidates.Any(c => c.Real && Math.Abs((x.X * p.Size.Width) - c.X) < 4 && Math.Abs((x.Y * p.Size.Height) - c.Y) < 4)).ToList();

    /// <summary>Added by the update: placed on the same spot as an added hold (the other panel shows it too).</summary>
    private static bool IsNew(ExportedHold hold, List<ExportedHold> added) =>
        added.Any(a => a.FacetId == hold.FacetId && a.PlaneAMm is { } aa && a.PlaneBMm is { } ab
            && Math.Sqrt(Math.Pow(aa - hold.PlaneAMm!.Value, 2) + Math.Pow(ab - hold.PlaneBMm!.Value, 2)) < 30);

    /// <summary>How many real and junk candidates a fixed catch distance (mm) to the nearest placed hold would take.</summary>
    private void CatchSweep(AtticFixturePanel panel, List<FacetRegistration> registrations, List<FacetSpot> known)
    {
        var nearest = panel.Candidates.ToDictionary(c => c.Id, c => registrations.Where(r => r.Accepted)
            .Select(r => (r.FacetId, P: r.Map(c.X / panel.Size.Width, c.Y / panel.Size.Height)))
            .Where(m => double.IsFinite(m.P.A))
            .SelectMany(m => known.Where(k => k.FacetId == m.FacetId).Select(k => Math.Sqrt(Math.Pow(k.A - m.P.A, 2) + Math.Pow(k.B - m.P.B, 2))))
            .DefaultIfEmpty(double.MaxValue).Min());
        foreach (var mm in new[] { 20, 30, 40, 50, 60, 75 })
        {
            output.WriteLine($"  catch {mm} mm: {panel.Candidates.Count(c => c.Real && nearest[c.Id] <= mm)} real, {panel.Candidates.Count(c => !c.Real && nearest[c.Id] <= mm)} junk");
        }
    }

    private static Dictionary<Guid, NewHoldDiscardReason> Classify(
        AtticFixturePanel panel, OverlapOwner? owner, IReadOnlyDictionary<Guid, Evidence3DVerdict>? verdicts)
    {
        var input = new NewHoldTriageInput(
            panel.Candidates.Select(c => new TriageCandidate(c.Id, c.X, c.Y)).ToList(), panel.Pairs, panel.OldSize, panel.Markers, owner, verdicts);
        var scores = panel.Candidates.ToDictionary(c => (c.X, c.Y), c => c.Presence);
        return NewHoldTriage.Classify(input, q => q.Select(x => scores.GetValueOrDefault((x.NewX, x.NewY))).ToList());
    }

    private void Report(string name, string label, AtticFixturePanel panel, IReadOnlyDictionary<Guid, string> discarded)
    {
        var real = panel.Candidates.Where(c => c.Real).ToList();
        var junk = panel.Candidates.Where(c => !c.Real).ToList();
        output.WriteLine(
            $"{name} {label}: kept {real.Count(c => !discarded.ContainsKey(c.Id))}/{real.Count} real, "
            + $"discarded {junk.Count(c => discarded.ContainsKey(c.Id))}/{junk.Count} junk "
            + $"({string.Join(", ", discarded.GroupBy(d => d.Value).Select(d => $"{d.Key} {d.Count()}"))})");
    }
}
