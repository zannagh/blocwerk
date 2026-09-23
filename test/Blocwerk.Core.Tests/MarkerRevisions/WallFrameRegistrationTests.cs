// <copyright file="WallFrameRegistrationTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using Xunit.Abstractions;
using static Blocwerk.Core.Tests.MarkerRevisions.RegistrationFixtures;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// Tying a new solve to the active model's frame: exact on a synthetic rigid offset, only unchanged
/// markers used (a changed marker reusing its id is excluded), refusal below three markers, and the
/// partial re-capture that carries unphotographed facets over.
/// </summary>
public class WallFrameRegistrationTests(ITestOutputHelper output)
{
    private static readonly RigidTransform3D Offset = Transform(4, 1.5, [320, -150, 60]);

    [Fact]
    public void SyntheticRigidOffset_IsUndone_AndFacetFramesAreTheReferenceOnes()
    {
        var solved = Move(Rev1Json, Offset, shiftFacet: "0", shiftAMm: 180);

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), null);
        var rewritten = WallFrameRegistrationWriter.Rewrite(solved, Rev1Json, result, null, Stamp());

        Assert.True(result.Accepted, result.Message);
        Assert.True(result.RmsMm < 0.05, $"rms {result.RmsMm}");
        Assert.Equal(Offset.RotationDeg, result.Transform!.RotationDeg, 3);
        var before = PlaneCentres(Rev1Json);
        var after = PlaneCentres(rewritten);
        Assert.All(before, kv =>
        {
            Assert.Equal(kv.Value.Facet, after[kv.Key].Facet);
            Assert.Equal(kv.Value.A, after[kv.Key].A, 1);
            Assert.Equal(kv.Value.B, after[kv.Key].B, 1);
        });
        var facet = Doc(rewritten).FindFacet("0")!.Value.Facet;
        Assert.Equal(Doc(Rev1Json).FindFacet("0")!.Value.Facet.Origin, facet.Origin);
    }

    [Fact]
    public void ChangedMarkerReusingItsId_IsExcluded_EvenThoughItMovedFar()
    {
        // Filler 4 was replaced by a 60 mm sheet 300 mm further right, under the same id.
        var solved = Displace(Move(Rev1Json, Offset), 4, 300, 0, scale: 0.48);
        var unchanged = Doc(Rev1Json).Markers.Select(m => m.Id).Where(id => id != 4).ToHashSet();

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), unchanged);
        var rewritten = WallFrameRegistrationWriter.Rewrite(solved, Rev1Json, result, unchanged, Stamp());

        Assert.True(result.Accepted, result.Message);
        Assert.Equal([4], result.ChangedIds);
        Assert.DoesNotContain(4, result.UsedIds);
        Assert.True(result.RmsMm < 0.05, $"rms {result.RmsMm}");
        Assert.Equal(PlaneCentres(Rev1Json)[4].A + 300, PlaneCentres(rewritten)[4].A, 0);
    }

    [Fact]
    public void WithoutTheRevisionFilter_TheSameMoveIsCaughtAsAnOutlier()
    {
        var solved = Displace(Move(Rev1Json, Offset), 4, 300, 0);

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), null);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal([4], result.OutlierIds);
    }

    [Fact]
    public void FewerThanThreeUnchangedMarkers_IsRefused_WithAClearReason()
    {
        var solved = Move(Rev1Json, Offset);

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), new HashSet<int> { 0, 2 });

        Assert.False(result.Accepted);
        Assert.Null(result.Transform);
        Assert.Contains("Only 2 marker(s) kept their place (0, 2)", result.Message);
        Assert.Contains("keep at least 3 unchanged", result.Message);
    }

    [Fact]
    public void MarkersInOneLine_AreRefused()
    {
        // 2, 3 and 27 all sit along the main wall's bottom edge (within a millimetre of one line).
        var solved = Move(Rev1Json, Offset);

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), new HashSet<int> { 2, 3, 27 });

        output.WriteLine(result.Message);
        Assert.False(result.Accepted);
        Assert.Contains("one line", result.Message);
    }

    [Fact]
    public void PartialRecapture_CarriesTheUnphotographedFacetOver()
    {
        var solved = Move(WithoutSegment(Rev1Json, 5), Offset);

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), null);
        var json = WallFrameRegistrationWriter.Rewrite(solved, Rev1Json, result, null, Stamp());
        var rewritten = Doc(json);

        Assert.True(result.Accepted, result.Message);
        Assert.NotNull(rewritten.FindFacet("5"));
        Assert.Equal(Doc(Rev1Json).Markers.Select(m => m.Id).Order(), rewritten.Markers.Select(m => m.Id));
        Assert.Equal(PlaneCentres(Rev1Json)[32], PlaneCentres(json)[32]);
        Assert.Contains("\"carriedFacets\":[\"5\"]", json);
        Assert.Empty(WallGeometryValidator.Validate(rewritten));
    }

    internal static WallGeometryDocument Doc(string json) => WallGeometryDocument.Parse(json);

    internal static RegistrationStamp Stamp() => new(Guid.NewGuid(), 1, 2);
}
