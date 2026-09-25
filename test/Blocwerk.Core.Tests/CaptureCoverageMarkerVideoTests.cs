// <copyright file="CaptureCoverageMarkerVideoTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.CoverageFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The marker and video parts of the capture coverage report: too few markers, markers seen in too few photos and
/// bunched markers get a "where to add" suggestion sized by the marker sizing rules; the capture recipe's passes are
/// checked on the video frames' poses when reported, else on the photos, and the missing ones are listed.
/// </summary>
public class CaptureCoverageMarkerVideoTests
{
    private static readonly double[] Middle = At(1500, 1500);

    private static CoverageCamera[] ChestHeight =>
        [.. new[] { 500.0, 1500, 2500 }.Select((x, i) => Photo([x, -2500, 1300], At(x, 1300), $"p{i}"))];

    [Fact]
    public void AFacetWithTwoMarkers_GetsOneMoreSuggested_AwayFromThem_WithASizeFromTheSizingRules()
    {
        var report = CaptureCoverageAnalyzer.Analyze(Inputs(Document((1, 300, 300, 6), (2, 600, 300, 2)), ChestHeight), DateTimeOffset.UnixEpoch);

        var markers = report.Facets.Single().Markers;
        Assert.True(markers.FewMarkers);
        Assert.Equal([2], markers.WeakMarkers);
        var suggestion = Assert.IsType<MarkerSuggestion>(markers.Suggestion);
        Assert.Equal(1, suggestion.Count);
        Assert.Contains(suggestion.SizeMm, MarkerGenerationOptions.Default.AvailableSizesMm);
        Assert.Equal("the top right", suggestion.Where);
        Assert.True(suggestion.Points[0][0] > 2500 && suggestion.Points[0][1] > 2500);
        Assert.Contains(report.Advice, a => a.Text == $"Add 1 marker ({suggestion.SizeMm:F0} mm) on the wall at the top right: it has only 2");
        Assert.Contains(report.Advice, a => a.Text.StartsWith("Marker 2 on the wall is in fewer than 3 photos", StringComparison.Ordinal));
    }

    [Fact]
    public void BunchedMarkers_OnALargeFacet_PoorlyPinItDown()
    {
        var bunched = Document((1, 200, 200, 8), (2, 400, 200, 8), (3, 300, 400, 8));
        var spread = Document((1, 200, 200, 8), (2, 2800, 200, 8), (3, 1500, 2800, 8));

        var poor = CaptureCoverageAnalyzer.Analyze(Inputs(bunched, ChestHeight), DateTimeOffset.UnixEpoch).Facets.Single().Markers;
        var fine = CaptureCoverageAnalyzer.Analyze(Inputs(spread, ChestHeight), DateTimeOffset.UnixEpoch).Facets.Single().Markers;

        Assert.True(poor.PoorSpread);
        Assert.False(poor.FewMarkers);
        Assert.Equal(2, poor.Suggestion!.Count);
        Assert.True(poor.SpreadRatio < 0.01);
        Assert.False(fine.PoorSpread);
        Assert.Null(fine.Suggestion);
        Assert.Empty(fine.WeakMarkers);
    }

    [Fact]
    public void TheMarkerSizeHint_FollowsThePhotoDistance()
    {
        var facet = Scene().Facets.Single();
        var near = MarkerCoverageRater.SizeHint(facet, [Photo([1500, -1500, 1500], Middle)]);
        var far = MarkerCoverageRater.SizeHint(facet, [Photo([1500, -6000, 1500], Middle)]);

        Assert.True(far > near, $"far {far} mm should need a bigger print than near {near} mm");
    }

    [Fact]
    public void PhotosOnlyAtChestHeight_MissTheKneeAndOverheadPasses_TheEndArcsAndTheVolumeSemicircle()
    {
        var report = CaptureCoverageAnalyzer.Analyze(Inputs(Document(), ChestHeight, [Block(1, 1500, 2200)]), DateTimeOffset.UnixEpoch);

        var passes = report.Video.Passes.ToDictionary(p => p.Key);
        Assert.Equal(CoveragePoseSource.Photos, report.Video.PosesFrom);
        Assert.True(passes["chest"].Present);
        Assert.False(passes["knee"].Present);
        Assert.False(passes["overhead"].Present);
        Assert.False(passes["under-volume-1"].Present);
        var video = report.Advice.Where(a => a.Kind == "video").Select(a => a.Text).ToList();
        Assert.Contains("Add a knee-height pass aimed up to the video: no photo was taken like that", video);
        Assert.Contains("Add an overhead pass aimed down to the video: no photo was taken like that", video);
        Assert.Contains("Walk a semicircle under volume 1 in the video: the photos do not see it from below all round", video);
    }

    [Fact]
    public void RegisteredVideoFramesWithPoses_AreCheckedInsteadOfThePhotos()
    {
        var knee = new[] { 500.0, 1500, 2500 }.Select((x, i) => LookAt($"vf_{i}", [x, -1500, 400], At(x, 1800)));
        var overhead = new[] { 500.0, 1500, 2500 }.Select((x, i) => LookAt($"vf_1{i}", [x, -1500, 2300], At(x, 900)));
        var under = new[] { -2500.0, 0, 2500 }.Select((dx, i) => LookAt($"vf_2{i}", [1500 + dx, -900, 700], At(1500, 2200)));
        var frames = CoverageCamera.FromSplatFrame(FrameJson(300, [.. knee, .. overhead, .. under]));

        var report = CaptureCoverageAnalyzer.Analyze(
            Inputs(Document(), ChestHeight, [Block(1, 1500, 2200)], frames, new CoverageVideoInput(true, 300, 300)), DateTimeOffset.UnixEpoch);

        var passes = report.Video.Passes.ToDictionary(p => p.Key);
        Assert.Equal(9, report.VideoViews);
        Assert.Equal(CoveragePoseSource.Video, report.Video.PosesFrom);
        Assert.True(passes["knee"].Present);
        Assert.True(passes["overhead"].Present);
        Assert.True(passes["under-volume-1"].Present, passes["under-volume-1"].Detail);
        Assert.Contains(report.Advice, a => a.Text == "The video has no chest-height pass");
    }

    [Fact]
    public void NoVideo_OrFewPlacedFrames_IsCalledOut()
    {
        var none = CaptureCoverageAnalyzer.Analyze(
            Inputs(Document(), ChestHeight, video: new CoverageVideoInput(false, 0, null)), DateTimeOffset.UnixEpoch);
        var few = CaptureCoverageAnalyzer.Analyze(
            Inputs(Document(), ChestHeight, video: new CoverageVideoInput(true, 300, CaptureCoverageService.RegisteredFrames(FrameJson(100)))),
            DateTimeOffset.UnixEpoch);

        Assert.Contains(none.Advice, a => a.Text.StartsWith("Add a walk-along video", StringComparison.Ordinal));
        Assert.Contains(few.Advice, a => a.Text == "Only 100 of 300 video frames could be placed: walk slower and keep the wall in view");
        Assert.Empty(CoverageCamera.FromSplatFrame("{\"stats\":{}}"));
        Assert.Null(CaptureCoverageService.RegisteredFrames("{"));
    }
}
