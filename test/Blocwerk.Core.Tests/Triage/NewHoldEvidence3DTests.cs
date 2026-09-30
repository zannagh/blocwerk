// <copyright file="NewHoldEvidence3DTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// The 3D verdict of an unpaired detection: the photo maps 1:1000 onto one 1 m facet (x → a, y → b, mm), so a
/// normalised photo point (0.52, 0.5) lands at (520, 500) on the facet.
/// </summary>
public class NewHoldEvidence3DTests
{
    private const string Facet = "0";

    [Fact]
    public void Judge_OnAPlacedHold_IsAKnownHold()
    {
        var evidence = Evidence(known: [new FacetSpot(Facet, 500, 500, 50)]);

        Assert.Equal(Evidence3DVerdict.KnownHold, NewHoldEvidence3D.Judge(evidence, 0.52, 0.5));
    }

    [Fact]
    public void Judge_BesideAPlacedHold_IsNothing()
    {
        var evidence = Evidence(known: [new FacetSpot(Facet, 500, 500, 50)]);

        Assert.Equal(Evidence3DVerdict.None, NewHoldEvidence3D.Judge(evidence, 0.6, 0.5));
    }

    [Fact]
    public void Judge_OnAVolume_TheCatchIsCapped_SoANewHoldOnItStaysNew()
    {
        var evidence = Evidence(known: [new FacetSpot(Facet, 500, 500, 400)]);

        Assert.Equal(Evidence3DVerdict.None, NewHoldEvidence3D.Judge(evidence, 0.6, 0.5));
    }

    [Fact]
    public void Judge_OnAnotherFacetsHold_IsNothing()
    {
        var evidence = Evidence(known: [new FacetSpot("1a", 500, 500, 50)]);

        Assert.Equal(Evidence3DVerdict.None, NewHoldEvidence3D.Judge(evidence, 0.5, 0.5));
    }

    [Fact]
    public void Judge_SeenIn3D_WinsOverAKnownHold()
    {
        var evidence = Evidence(known: [new FacetSpot(Facet, 500, 500, 50)], seen: [new FacetSpot(Facet, 510, 500, 30)]);

        Assert.Equal(Evidence3DVerdict.SeenIn3D, NewHoldEvidence3D.Judge(evidence, 0.5, 0.5));
    }

    [Fact]
    public void Judge_BelowTheWall_WithTheRayMissingEveryFacet_IsOffTheWall()
    {
        var camera = new SyntheticWallCamera();
        var (x, y) = camera.Project(1500, 0, -450);

        Assert.Equal(Evidence3DVerdict.OffWall, NewHoldEvidence3D.Judge(camera.Evidence(), x, y));
    }

    [Fact]
    public void Judge_OnAFacetThatDidNotRegister_IsNotOffTheWall()
    {
        var camera = new SyntheticWallCamera();
        var (x, y) = camera.OnFacet1(1500, 1500);
        var (a, _) = camera.Registration().Map(x, y);

        Assert.True(a > 2000 + NewHoldEvidence3D.OffWallMm, "through facet 0's mapping the spot lies far off facet 0");
        Assert.Equal(Evidence3DVerdict.None, NewHoldEvidence3D.Judge(camera.Evidence(), x, y));
    }

    [Fact]
    public void Judge_WithoutTheModelsFacets_NothingIsOffTheWall()
    {
        var camera = new SyntheticWallCamera();
        var (x, y) = camera.Project(1500, 0, -450);

        Assert.Equal(Evidence3DVerdict.None, NewHoldEvidence3D.Judge(camera.Evidence() with { Facets = null }, x, y));
    }

    [Fact]
    public void Judge_WithoutAnAcceptedRegistration_IsNothing()
    {
        var evidence = new Panel3DEvidence(
            [FacetRegistration.Rejected(Facet, new PlaneRectMm(0, 1000, 0, 1000), 10, 0, "too few inliers")],
            [new FacetSpot(Facet, 500, 500, 50)],
            []);

        Assert.False(evidence.IsUsable);
        Assert.Equal(Evidence3DVerdict.None, NewHoldEvidence3D.Judge(evidence, 0.5, 0.5));
        Assert.Equal(Evidence3DVerdict.None, NewHoldEvidence3D.Judge(evidence, 0.5, 3));
    }

    internal static FacetRegistration Registration(string facetId = Facet) =>
        new(
            facetId,
            true,
            200,
            150,
            80,
            0.8,
            0.6,
            3,
            null,
            PlaneHomography.FromCoefficients([1000, 0, 0, 0, 1000, 0, 0, 0, 1]),
            new PlaneRectMm(0, 1000, 0, 1000));

    private static Panel3DEvidence Evidence(IReadOnlyList<FacetSpot>? known = null, IReadOnlyList<FacetSpot>? seen = null) =>
        new([Registration()], known ?? [], seen ?? []);
}
