// <copyright file="MarkerRevisionInferenceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerRevisions.RevisionFixtures;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// Which revision a photo shows is read from its markers, not from the save date: revision 2 is saved but the
/// wall may still carry revision 1's sheets. Ids, sizes relative to unchanged neighbours, and — only as a
/// tie-breaker — the "markers swapped on the wall" date decide.
/// </summary>
public class MarkerRevisionInferenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OldSheetsStillUp_PhotoIsRevision1()
    {
        var evidence = MarkerRevisionInference.Infer(Observed(Rev1, 0, 1, 4, 5, 24), Candidates(), Now)!;

        Assert.Equal((1, 1, 1), (evidence.Revision, evidence.CompatibleFrom, evidence.CompatibleTo));
        Assert.True(evidence.IsDecisive);
        Assert.Equal([4, 5], evidence.DecidingIds);
    }

    [Fact]
    public void NewId_PhotoIsRevision2()
    {
        var evidence = MarkerRevisionInference.Infer(Observed(Rev2, 0, 1, 44, 24), Candidates(), Now)!;

        Assert.Equal((2, 2, 2), (evidence.Revision, evidence.CompatibleFrom, evidence.CompatibleTo));
        Assert.Equal(MarkerRevisionInference.IdCost, evidence.Costs[1], 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ResizedFiller_IsToldApartBySizeNextToUnchangedNeighbours(int shown)
    {
        // Same ids either way: only filler 4's apparent size next to 0, 1 and 24 says 125 mm or 60 mm.
        var plan = shown == 1 ? Rev1 : Rev2;
        var evidence = MarkerRevisionInference.Infer(Observed(plan, 0, 1, 4, 24), Candidates(), Now)!;

        Assert.Equal(shown, evidence.Revision);
        Assert.True(evidence.IsDecisive);
        Assert.True(Math.Abs(evidence.Costs[1] - evidence.Costs[2]) > Math.Log(2), "a 125 → 60 mm resize is a strong signal");
    }

    [Fact]
    public void OnlyUnchangedMarkers_IsCompatibleWithBoth_AndTheSwapDateBreaksTheTie()
    {
        var observed = Observed(Rev1, 0, 1, 24);

        var undated = MarkerRevisionInference.Infer(observed, Candidates(), Now)!;
        var rev1Up = MarkerRevisionInference.Infer(observed, Candidates(rev1From: Now.AddDays(-30)), Now)!;
        var rev2Up = MarkerRevisionInference.Infer(observed, Candidates(Now.AddDays(-30), Now.AddDays(-1)), Now)!;

        Assert.Equal((1, 2), (undated.CompatibleFrom, undated.CompatibleTo));
        Assert.False(undated.IsDecisive);
        Assert.Empty(undated.DecidingIds);
        Assert.Equal((2, 1, 2), (undated.Revision, rev1Up.Revision, rev2Up.Revision));
    }

    [Fact]
    public void Evidence_BeatsTheSwapDate()
    {
        // Marked as swapped yesterday, but the photo still shows the 125 mm filler: the photo wins.
        var evidence = MarkerRevisionInference.Infer(Observed(Rev1, 0, 1, 4, 24), Candidates(Now.AddDays(-30), Now.AddDays(-1)), Now)!;

        Assert.Equal(1, evidence.Revision);
    }

    [Fact]
    public void Capture_RefusesARevisionThePhotosContradict_AndAcceptsTheOneTheyShow()
    {
        // Solved as revision 2 (filler 4 at 60 mm), but the solve measures it at 125 mm: revision 1's sheet is up.
        var solved = Solved(4, 125);

        var refusal = CaptureRevisionCheck.Refusal(solved, 2, Candidates(), Now);

        Assert.NotNull(refusal);
        Assert.Contains("show the markers of plan revision 1, but this capture uses revision 2", refusal);
        Assert.Contains("marker 4 measures ≈125 mm where revision 2 plans 60 mm (revision 1: 125 mm)", refusal);
        Assert.Null(CaptureRevisionCheck.Refusal(solved, 1, Candidates(), Now));
        Assert.Null(CaptureRevisionCheck.Refusal(Solved(4, 61), 2, Candidates(), Now));
    }

    [Fact]
    public void Capture_WithOnlyUnchangedMarkersMeasured_IsNotRefused()
    {
        var solved = Solved(null, 0);

        Assert.Null(CaptureRevisionCheck.Refusal(solved, 2, Candidates(), Now));
    }

    /// <summary>The revision-1 solve with every marker measured at its planned 125 mm, except <paramref name="id"/>.</summary>
    private static WallGeometryDocument Solved(int? id, double measuredMm)
    {
        var document = WallGeometryDocument.Parse(RegistrationFixtures.Rev1Json);
        var markers = document.Markers
            .Where(m => id is not null || m.Id is not (4 or 5))
            .Select(m => m with { MeasuredSideMm = m.Id == id ? measuredMm : 125.4, MeasuredSidePhotos = 4 })
            .ToList();
        return document with { Markers = markers };
    }
}
