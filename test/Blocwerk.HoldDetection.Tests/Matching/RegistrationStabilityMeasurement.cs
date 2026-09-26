using System.Diagnostics;
using System.Text.Json;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.HoldDetection.Tests.Outlines;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// Read-only measurement of how stable one panel photo's registration is: <c>BLOCWERK_STABILITY_DIR</c> is an export
/// as for <see cref="AtticTextureRegistrationMeasurement"/>; <c>BLOCWERK_STABILITY_PANEL</c> (default 1) picks the
/// panel column and <c>BLOCWERK_STABILITY_RUNS</c> (default 5) how often it is registered, each time with a fresh
/// session, without anchors (the direct match every run starts with). Prints every facet's inliers and coverage per
/// run. Skipped otherwise.
/// </summary>
public class RegistrationStabilityMeasurement(ITestOutputHelper output)
{
    [SkippableFact]
    public void DryRun_RegistersOnePhotoRepeatedly()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_STABILITY_DIR");
        var texturesJson = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "textures", "textures.json");
        Skip.If(texturesJson is null || !File.Exists(texturesJson), "Set BLOCWERK_STABILITY_DIR to an export with textures/textures.json.");
        var col = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_STABILITY_PANEL"), out var c) ? c : 1;
        var runs = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_STABILITY_RUNS"), out var r) ? r : 5;
        var generation = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_GENERATION"), out var g) ? g : 3;
        var textures = AtticTextureRegistrationMeasurement.LoadTextures(dir!, texturesJson!);
        var rows = JsonSerializer.Deserialize<List<AtticExportedHold>>(File.ReadAllText(Path.Combine(dir!, "holds.json")))!;
        var holds = rows.Where(h => h.Generation == generation && h.PanelCol == col && h.WallPanelId is not null).Select(h => h.ToHold()).ToList();
        var photo = File.ReadAllBytes(Path.Combine(dir!, "photos", $"panel_c{col}_r0_g{generation}.jpg"));
        for (var run = 1; run <= runs; run++)
        {
            var watch = Stopwatch.StartNew();
            using var session = new OpenCvPhotoTextureMatcher().OpenPhoto(photo);
            var registrations = new PhotoRegistrar(session, NullLogger.Instance, $"c{col}").RegisterAll(textures);
            var placed = holds.Count(h => HoldTexturePlacer.Place(h, registrations) is not null);
            output.WriteLine($"run {run} ({watch.Elapsed.TotalSeconds:F0} s): {placed} of {holds.Count} placed; " + string.Join(
                "; ", registrations.Select(x => $"facet {x.FacetId}: {x.Inliers} inliers, {x.Coverage:P0}{(x.Accepted ? string.Empty : " (refused)")}")));
        }
    }
}
