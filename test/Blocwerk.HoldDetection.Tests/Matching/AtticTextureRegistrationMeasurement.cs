using System.Text.Json;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.HoldDetection.Tests.Outlines;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// Read-only dry run of "place existing holds on the 3D model" against an exported wall (never committed):
/// <c>BLOCWERK_ATTIC_DIR</c> holds <c>holds.json</c> and <c>photos/panel_c{col}_r{row}_g{gen}.jpg</c> as for the
/// outline measurement, plus <c>textures/textures.json</c> — an array of the model's texture rows
/// (<c>facetId, file, maskFile, aMin, aMax, bMin, bMax, widthPx, heightPx</c>, files next to it) — and optionally
/// <c>model.json</c>, the active model (facet extents and 3D frames, so facets can be predicted from their
/// neighbours as the service does). <c>BLOCWERK_ATTIC_GENERATION</c> picks the live generation (default 3).
/// Skipped otherwise.
/// </summary>
public class AtticTextureRegistrationMeasurement(ITestOutputHelper output)
{
    [SkippableFact]
    public void DryRun_RegistersThePanelPhotosOntoTheTextures()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_DIR");
        var texturesJson = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "textures", "textures.json");
        Skip.If(texturesJson is null || !File.Exists(texturesJson), "Set BLOCWERK_ATTIC_DIR to an export with textures/textures.json.");
        var generation = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_GENERATION"), out var g) ? g : 3;
        var textures = LoadTextures(dir!, texturesJson!);
        var rows = JsonSerializer.Deserialize<List<AtticExportedHold>>(File.ReadAllText(Path.Combine(dir!, "holds.json")))!;

        foreach (var panel in rows.Where(r => r.Generation == generation && r.WallPanelId is not null)
                     .GroupBy(r => (Col: r.PanelCol ?? 0, Row: r.PanelRow ?? 0)).OrderBy(p => p.Key))
        {
            var photo = File.ReadAllBytes(Path.Combine(dir!, "photos", $"panel_c{panel.Key.Col}_r{panel.Key.Row}_g{generation}.jpg"));
            using var session = new OpenCvPhotoTextureMatcher().OpenPhoto(photo);
            var registrar = new PhotoRegistrar(session, new TestOutputLogger(output), $"c{panel.Key.Col}");
            var registrations = registrar.RegisterAll(textures);
            var holds = panel.Select(r => r.ToHold()).ToList();
            var placed = holds.Select(x => HoldTexturePlacer.Place(x, registrations)).Where(f => f is not null).ToList();
            output.WriteLine($"panel c{panel.Key.Col}: {placed.Count} of {holds.Count} holds placed "
                + string.Join(", ", placed.GroupBy(f => f!.FacetId).Select(f => $"facet {f.Key}: {f.Count()}")));
        }
    }

    private static List<RegistrationTexture> LoadTextures(string dir, string texturesJson)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var rows = JsonSerializer.Deserialize<List<AtticExportedTexture>>(File.ReadAllText(texturesJson), options)!;
        var modelPath = Path.Combine(dir, "model.json");
        var doc = File.Exists(modelPath) ? WallGeometryDocument.Parse(File.ReadAllText(modelPath)) : null;
        var extents = doc is null ? [] : Wall3DFallbackPlacement.FacetExtents(doc);
        var frames = doc?.Segments.SelectMany(s => s.Facets).Where(f => f.Id is not null)
            .ToDictionary(f => f.Id!, FacetFrame.From) ?? [];
        var folder = Path.Combine(dir, "textures");
        return rows.Select(t => new RegistrationTexture(
            new TexturePlaneFrame(t.FacetId, t.AMin, t.AMax, t.BMin, t.BMax, t.WidthPx, t.HeightPx),
            extents.TryGetValue(t.FacetId, out var e) ? e : new PlaneRectMm(t.AMin, t.AMax, t.BMin, t.BMax),
            File.ReadAllBytes(Path.Combine(folder, t.File)),
            t.MaskFile is null ? null : File.ReadAllBytes(Path.Combine(folder, t.MaskFile)),
            frames.GetValueOrDefault(t.FacetId))).ToList();
    }
}
