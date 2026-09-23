// <copyright file="MarkerPlanDiffTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Tests.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// The per-marker comparison between revisions, and The Attic's legacy → plan transition: revision 1 built
/// from the measured wall is the legacy layout exactly, and a later "shrink the fillers, add four markers"
/// revision diffs as such.
/// </summary>
public class MarkerPlanDiffTests
{
    private static readonly string AtticGeometry = File.ReadAllText(WallMarkerLayoutTests.Fixture("attic-wall-geometry.json"));

    [Fact]
    public void Diff_ClassifiesEveryKindOfChange_AndAReusedIdIsNeverUnchanged()
    {
        PlanMarker[] before =
        [
            new(1, 0, 100, 100, 125, MarkerRole.Corner), new(2, 0, 500, 100, 125, MarkerRole.Filler),
            new(3, 0, 900, 100, 125, MarkerRole.Filler), new(4, 1, 100, 100, 125, MarkerRole.Corner),
            new(5, 1, 400, 100, 125, MarkerRole.Filler), new(6, 1, 700, 100, 125, MarkerRole.Filler),
        ];
        PlanMarker[] after =
        [
            new(1, 0, 104, 103, 125, MarkerRole.Corner), // 5 mm: within tolerance
            new(2, 0, 500, 100, 60, MarkerRole.Filler), // shrunk, same id
            new(3, 0, 960, 100, 125, MarkerRole.Filler), // moved
            new(4, 2, 100, 100, 125, MarkerRole.Corner), // other surface
            new(6, 1, 740, 100, 80, MarkerRole.Filler), // moved AND resized
            new(7, 1, 900, 100, 60, MarkerRole.Filler), // new
        ];

        var diff = MarkerPlanDiff.Compare(before, after);

        Assert.Equal([1], diff.UnchangedIds);
        Assert.Equal([7], diff.Added);
        Assert.Equal([5], diff.Removed);
        Assert.Equal([2, 6], diff.Resized);
        Assert.Equal([3, 6], diff.Moved);
        Assert.Equal([4], diff.Reassigned);
        Assert.Equal([2, 3, 4, 6, 7], diff.ToPrint);
        Assert.Equal(MarkerChange.Moved | MarkerChange.Resized, diff.Entries.Single(e => e.Id == 6).Change);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void LegacyToPlan_RevisionOneFromTheMeasuredWall_IsTheLegacyLayoutExactly()
    {
        var document = WallGeometryDocument.Parse(AtticGeometry);
        var plan = MarkerPlanFromGeometry.Build(document, AtticMarkerPlan.Photo);
        var roundTripped = MarkerPlanJson.FromJson(MarkerPlanJson.ToJson(plan), out var errors);
        var legacy = MarkerBaselines.TryLegacyMarkers(AtticGeometry)!;

        Assert.Empty(errors);
        Assert.True(MarkerPlanDiff.Compare(legacy, roundTripped!.Markers).IsEmpty);
        Assert.Equal(document.Markers.Select(m => m.Id).Order(), roundTripped.Markers.Select(m => m.Id).Order());
        Assert.All(roundTripped.Markers, m =>
        {
            var measured = document.FindMarker(m.Id)!;
            var facet = document.FindFacet(measured.Facet)!.Value;
            Assert.Equal(facet.Segment.Index, m.Segment);
            Assert.Equal(125, m.SizeMm);
            Assert.Equal(measured.CornersPlaneMm.Average(c => c[0]) - facet.Facet.ExtentMm!.Value.AMin, m.XMm, tolerance: 0.06);
            Assert.Equal(measured.CornersPlaneMm.Average(c => c[1]) - facet.Facet.ExtentMm!.Value.BMin, m.YMm, tolerance: 0.06);
        });
        Assert.Equal(WallMarkerLayout.LegacyIdScheme, document.IdScheme);
    }

    [Fact]
    public void LaterRevision_ShrinkingFillersAndAddingFourMarkers_DiffsAsExactlyThat()
    {
        var rev1 = MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(AtticGeometry), AtticMarkerPlan.Photo);
        var fillers = rev1.Markers.Where(m => m.Role == MarkerRole.Filler).Select(m => m.Id).ToHashSet();
        var added = Enumerable.Range(40, 4).Select(id => new PlanMarker(id, 0, 600 + (id * 50), 1600, 60, MarkerRole.Filler));
        var rev2 = rev1 with
        {
            Markers = rev1.Markers.Select(m => fillers.Contains(m.Id) ? m with { SizeMm = 60 } : m).Concat(added).ToList(),
        };

        var diff = MarkerPlanDiff.Compare(rev1.Markers, rev2.Markers);

        Assert.NotEmpty(fillers);
        Assert.Equal(fillers.Order(), diff.Resized);
        Assert.Equal([40, 41, 42, 43], diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Moved);
        Assert.Equal(rev1.Markers.Select(m => m.Id).Except(fillers).Order(), diff.UnchangedIds.Order());
        Assert.True(diff.UnchangedIds.Count >= MarkerPlanChanges.MinUnchangedMarkers);
        var changes = MarkerPlanChanges.Build(1, diff);
        Assert.Contains(changes.Advice, a => a.Contains("keep at least 3 unchanged markers"));
    }
}
