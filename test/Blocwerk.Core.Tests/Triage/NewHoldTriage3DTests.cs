// <copyright file="NewHoldTriage3DTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// The triage with 3D verdicts: a detection on an existing hold's placement or far off the wall is discarded by
/// default; a marker still wins; "seen in 3D" never overrules the photo rules; without verdicts nothing changes.
/// </summary>
public class NewHoldTriage3DTests
{
    private static readonly Guid Known = Guid.NewGuid();
    private static readonly Guid OffWall = Guid.NewGuid();
    private static readonly Guid Seen = Guid.NewGuid();
    private static readonly Guid Plain = Guid.NewGuid();
    private static readonly Guid OnMarker = Guid.NewGuid();

    private static readonly IReadOnlyList<(double X, double Y)> Marker = [(900, 900), (1000, 900), (1000, 1000), (900, 1000)];

    [Fact]
    public void Classify_DiscardsKnownHoldsAndOffWall_KeepsTheRest()
    {
        var result = NewHoldTriage.Classify(Input(Verdicts()), null);

        Assert.Equal(NewHoldDiscardReason.KnownHoldIn3D, result[Known]);
        Assert.Equal(NewHoldDiscardReason.OffWallIn3D, result[OffWall]);
        Assert.Equal(NewHoldDiscardReason.OnMarker, result[OnMarker]);
        Assert.False(result.ContainsKey(Seen));
        Assert.False(result.ContainsKey(Plain));
    }

    [Fact]
    public void Classify_WithoutVerdicts_IsThePhotoOnlyTriage()
    {
        var result = NewHoldTriage.Classify(Input(null), null);

        Assert.Equal([OnMarker], result.Keys);
    }

    [Fact]
    public void Classify_SeenIn3D_DoesNotOverruleTheOldPhoto()
    {
        var input = Input(Verdicts()) with { NewToOld = Identity(), OldSize = (2000, 2000) };

        var result = NewHoldTriage.Classify(input, q => q.Select(_ => (double?)0.9).ToList());

        Assert.Equal(NewHoldDiscardReason.UnchangedSinceOldPhoto, result[Seen]);
        Assert.Equal(NewHoldDiscardReason.KnownHoldIn3D, result[Known]);
    }

    private static Dictionary<Guid, Evidence3DVerdict> Verdicts() => new()
    {
        [Known] = Evidence3DVerdict.KnownHold,
        [OffWall] = Evidence3DVerdict.OffWall,
        [Seen] = Evidence3DVerdict.SeenIn3D,
        [Plain] = Evidence3DVerdict.None,
        [OnMarker] = Evidence3DVerdict.KnownHold,
    };

    private static NewHoldTriageInput Input(IReadOnlyDictionary<Guid, Evidence3DVerdict>? verdicts) =>
        new(
            [
                new TriageCandidate(Known, 100, 100),
                new TriageCandidate(OffWall, 200, 1900),
                new TriageCandidate(Seen, 500, 500),
                new TriageCandidate(Plain, 700, 700),
                new TriageCandidate(OnMarker, 950, 950),
            ],
            [],
            null,
            [Marker],
            null,
            verdicts);

    private static List<PointPair> Identity() =>
        Enumerable.Range(0, 5).SelectMany(i => Enumerable.Range(0, 5).Select(j => new PointPair(i * 400, j * 400, i * 400, j * 400))).ToList();
}
