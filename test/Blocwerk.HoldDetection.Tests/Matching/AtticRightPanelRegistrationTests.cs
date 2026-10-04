using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.HoldDetection.Tests.Outlines;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// Real-data regression for The Attic's right panel photo (an oblique wide-angle shot whose coarse overlap is weak):
/// its registration onto the big facet "0" must keep growing past three refinement rounds. Local only, because the
/// gym's photos are not committed: <c>BLOCWERK_ATTIC_DIR</c> holds <c>textures/textures.json</c> (as for
/// <see cref="AtticTextureRegistrationMeasurement"/>) and <c>photos/panel_c0_r0_g{generation}.jpg</c> /
/// <c>panel_c1_r0_g{generation}.jpg</c>. Skipped otherwise.
/// </summary>
public class AtticRightPanelRegistrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void RightPanel_RegistersFacetZeroWithMoreInliersAndTheLeftPanelStaysStrong()
    {
        var dir = Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_DIR");
        var texturesJson = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "textures", "textures.json");
        Skip.If(texturesJson is null || !File.Exists(texturesJson), "Set BLOCWERK_ATTIC_DIR to an export with textures/textures.json.");
        var generation = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_GENERATION"), out var g) ? g : 3;
        var facetZero = AtticTextureRegistrationMeasurement.LoadTextures(dir!, texturesJson!).Where(t => t.Frame.FacetId == "0").ToList();

        var right = Register(dir!, 1, generation, facetZero);
        var left = Register(dir!, 0, generation, facetZero);

        // Before: 402 inliers on the right (three rounds), 2776 on the left.
        Assert.True(right.Accepted);
        Assert.True(right.Inliers >= 550, $"right panel facet 0: {right.Inliers} inliers");
        Assert.True(left.Accepted);
        Assert.True(left.Inliers >= 2600, $"left panel facet 0: {left.Inliers} inliers");
    }

    private FacetRegistration Register(string dir, int col, int generation, List<RegistrationTexture> textures)
    {
        var photo = File.ReadAllBytes(Path.Combine(dir, "photos", $"panel_c{col}_r0_g{generation}.jpg"));
        using var session = new OpenCvPhotoTextureMatcher().OpenPhoto(photo);
        var r = new PhotoRegistrar(session, new TestOutputLogger(output), $"c{col}").RegisterAll(textures).Single();
        output.WriteLine($"c{col}: {r.Inliers} inliers, coverage {r.Coverage:P0}");
        return r;
    }
}
