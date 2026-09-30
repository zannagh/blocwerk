// <copyright file="PanelPhotoPickerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// Sorting a visit's photos to panels: the photo that shows the whole panel from the same angle wins, a partial or
/// skewed view ranks lower, a photo serves one panel only, and a panel nothing matches keeps its photo.
/// </summary>
public class PanelPhotoPickerTests
{
    private static readonly byte[] PanelA = CaptureScenario.TinyJpeg(1);
    private static readonly byte[] PanelB = CaptureScenario.TinyJpeg(2);

    [Fact]
    public async Task EachPanel_GetsTheFrontalFullCoveragePhoto()
    {
        var full = Photo(10);
        var partial = Photo(11);
        var other = Photo(12);
        var unrelated = Photo(13);
        var alignment = new ScriptedAlignment()
            .With(full.Bytes, PanelA, Identity())
            .With(partial.Bytes, PanelA, Shift(0.45))
            .With(other.Bytes, PanelB, Scale(0.9));

        var picks = await Pick(alignment, full, partial, other, unrelated);

        var a = picks.Single(p => p.Col == 0);
        var b = picks.Single(p => p.Col == 1);
        Assert.Equal(full.Id, a.PhotoId);
        Assert.Equal(PanelPickConfidence.High, a.Confidence);
        Assert.Equal([full.Id, partial.Id], a.Candidates.Select(c => c.PhotoId));
        Assert.Equal(other.Id, b.PhotoId);
        Assert.Equal(PanelPickConfidence.High, b.Confidence);
    }

    [Fact]
    public async Task APhotoBestForTwoPanels_GoesToTheBetterOne_TheOtherTakesItsRunnerUp()
    {
        var shared = Photo(20);
        var second = Photo(21);
        var alignment = new ScriptedAlignment()
            .With(shared.Bytes, PanelA, Identity())
            .With(shared.Bytes, PanelB, Scale(0.7))
            .With(second.Bytes, PanelB, Scale(0.6));

        var picks = await Pick(alignment, shared, second);

        Assert.Equal(shared.Id, picks.Single(p => p.Col == 0).PhotoId);
        Assert.Equal(second.Id, picks.Single(p => p.Col == 1).PhotoId);
    }

    [Fact]
    public async Task APanelNothingCovers_KeepsItsPhoto()
    {
        var sliver = Photo(30);
        var alignment = new ScriptedAlignment().With(sliver.Bytes, PanelA, Shift(0.8));

        var picks = await Pick(alignment, sliver);

        Assert.All(picks, p => Assert.Null(p.PhotoId));
        Assert.All(picks, p => Assert.Equal(PanelPickConfidence.None, p.Confidence));
    }

    [Fact]
    public void Score_ASkewedView_IsLessFrontalThanAStraightOne()
    {
        var straight = PanelPhotoScore.Score(Guid.NewGuid(), Scale(0.9), 1);
        var skewed = PanelPhotoScore.Score(Guid.NewGuid(), new Homography([0.8, 0, 0.1, 0, 0.8, 0.1, 0.6, 0, 1], 100, 0.9), 1);

        Assert.Equal(1, straight.Frontal, 3);
        Assert.True(skewed.Frontal < 0.7, $"frontal {skewed.Frontal}");
        Assert.True(skewed.Score < straight.Score);
    }

    private static Task<IReadOnlyList<PanelPick>> Pick(ScriptedAlignment alignment, params (Guid Id, byte[] Bytes)[] photos)
    {
        var bytes = photos.ToDictionary(p => p.Id, p => p.Bytes);
        return new PanelPhotoPicker(alignment).PickAsync(
            [new PanelPhotoTarget(0, 0, PanelA), new PanelPhotoTarget(1, 0, PanelB)],
            photos.Select(p => new PhotoToSort(p.Id, 100)).ToList(),
            (id, _) => Task.FromResult<byte[]?>(bytes[id]),
            null,
            CancellationToken.None);
    }

    private static (Guid Id, byte[] Bytes) Photo(int seed) => (Guid.NewGuid(), CaptureScenario.TinyJpeg(seed));

    private static Homography Identity() => new([1, 0, 0, 0, 1, 0, 0, 0, 1], 200, 0.9);

    private static Homography Shift(double dx) => new([1, 0, dx, 0, 1, 0, 0, 0, 1], 200, 0.9);

    private static Homography Scale(double s) => new([s, 0, (1 - s) / 2, 0, s, (1 - s) / 2, 0, 0, 1], 200, 0.9);
}
