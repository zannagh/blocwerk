// <copyright file="NewHoldTriageTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The default discard of unpaired staged detections: on a printed marker, outside what the old photo
/// covered, or unchanged since the old photo. A real new hold inside the covered area that looks
/// different stays kept, and without an alignment only the marker rule applies.
/// </summary>
public class NewHoldTriageTests
{
    private static readonly Guid Marker = Guid.NewGuid();
    private static readonly Guid OffPanel = Guid.NewGuid();
    private static readonly Guid Unchanged = Guid.NewGuid();
    private static readonly Guid RealNew = Guid.NewGuid();

    [Fact]
    public void Classify_DiscardsMarkerOutsideAndUnchanged_KeepsTheRealNewHold()
    {
        var result = NewHoldTriage.Classify(Input(Shifted()), Presence);

        Assert.Equal(NewHoldDiscardReason.OnMarker, result[Marker]);
        Assert.Equal(NewHoldDiscardReason.OutsideOldPhoto, result[OffPanel]);
        Assert.Equal(NewHoldDiscardReason.UnchangedSinceOldPhoto, result[Unchanged]);
        Assert.False(result.ContainsKey(RealNew));
    }

    [Fact]
    public void Classify_WithoutAlignment_OnlyTheMarkerRuleApplies()
    {
        var result = NewHoldTriage.Classify(Input([]), Presence);

        Assert.Equal([Marker], result.Keys);
    }

    [Fact]
    public void Classify_WithoutAPresenceProbe_KeepsTheLookAlike()
    {
        var result = NewHoldTriage.Classify(Input(Shifted()), null);

        Assert.False(result.ContainsKey(Unchanged));
        Assert.False(result.ContainsKey(RealNew));
    }

    [Fact]
    public void LocalAffine_RecoversAShiftAndScale()
    {
        var p = LocalAffine.Predict(Shifted(), 1500, 1200)!.Value;

        Assert.Equal((1500 * 0.8) + 100, p.X, 3);
        Assert.Equal((1200 * 0.8) - 50, p.Y, 3);
        Assert.Equal(0.8, LocalAffine.Scale(Shifted(), 1500, 1200)!.Value, 3);
    }

    // The new photo maps onto the old one by old = 0.8·new + (100, -50), on a 2000×1500 old photo.
    private static List<PointPair> Shifted()
    {
        var pairs = new List<PointPair>();
        for (var x = 200; x <= 2400; x += 400)
        {
            for (var y = 200; y <= 1800; y += 400)
            {
                pairs.Add(new PointPair(x, y, (x * 0.8) + 100, (y * 0.8) - 50));
            }
        }

        return pairs;
    }

    private static NewHoldTriageInput Input(IReadOnlyList<PointPair> pairs) => new(
        [
            new TriageCandidate(Marker, 1000, 1000),
            new TriageCandidate(OffPanel, 2900, 900),
            new TriageCandidate(Unchanged, 800, 600),
            new TriageCandidate(RealNew, 1400, 1300),
        ],
        pairs,
        (2000, 1500),
        [[(980, 980), (1020, 980), (1020, 1020), (980, 1020)]]);

    private static IReadOnlyList<double?> Presence(IReadOnlyList<PresenceQuery> queries) =>
        queries.Select(q => q.NewX == 800 ? 0.92 : (double?)0.3).ToList();
}
