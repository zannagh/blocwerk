// <copyright file="QuickUpdateDefaultsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The quick review decides exactly what the step-by-step review starts from: every old hold carried (onto its
/// matcher twin where there is one), suggested discards dropped, and only overlaps of 90 % or more linked.
/// It never removes an old hold or marks one as moved.
/// </summary>
public class QuickUpdateDefaultsTests
{
    private readonly Guid centre = Guid.NewGuid();
    private readonly Guid right = Guid.NewGuid();

    [Fact]
    public void EveryOldHold_IsCarried_OntoItsTwin_NeverRemoved()
    {
        var (oldA, oldB, twinA) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var session = Session([new CarryoverProposal(oldA, twinA, 0.4, 3)], [oldA, oldB], []);

        var quick = QuickUpdateDefaults.Build(session, Holds((centre, [twinA])));

        Assert.Equal(2, quick.Carryover.Count);
        Assert.All(quick.Carryover, d => Assert.Equal(CarryKind.Carried, d.Kind));
        Assert.Equal(twinA, quick.Carryover.Single(d => d.OldHoldId == oldA).NewHoldId);
        Assert.Null(quick.Carryover.Single(d => d.OldHoldId == oldB).NewHoldId);
        Assert.Empty(quick.AcceptedNewCentreHoldIds);
    }

    [Fact]
    public void NewDetections_AreKept_UnlessTheServiceSuggestedDroppingThem()
    {
        var (fresh, onMarker) = (Guid.NewGuid(), Guid.NewGuid());
        var suggested = new Dictionary<Guid, NewHoldDiscardReason> { [onMarker] = NewHoldDiscardReason.OnMarker };
        var session = Session([], [], []) with { SuggestedNewDiscards = suggested };

        var quick = QuickUpdateDefaults.Build(session, Holds((centre, [fresh, onMarker])));

        Assert.Equal([fresh], quick.AcceptedNewCentreHoldIds);
        Assert.Equal([onMarker], quick.RemovedNewCentreHoldIds);
    }

    [Fact]
    public void Overlaps_AreLinkedOnlyFrom90Percent_AndNeverWhenMarkedMoved()
    {
        var (c1, c2, c3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (r1, r2, r3, junk) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var overlap = new NeighbourOverlap(right, 1, 0,
        [
            new OverlapProposalDto(centre, c1, r1, 0.95, false, 2),
            new OverlapProposalDto(centre, c2, r2, 0.8, false, 2),
            new OverlapProposalDto(centre, c3, r3, 0.97, true, 30),
        ]);
        var suggested = new Dictionary<Guid, NewHoldDiscardReason> { [junk] = NewHoldDiscardReason.UnchangedSinceOldPhoto };
        var session = Session([], [], [overlap]) with { SuggestedNewDiscards = suggested };

        var quick = QuickUpdateDefaults.Build(session, Holds((centre, [c1, c2, c3]), (right, [r1, r2, r3, junk])));

        var set = Assert.Single(quick.Neighbours);
        var link = Assert.Single(set.Links);
        Assert.Equal((c1, r1, false), (link.NeighborHoldId, link.NewHoldId, link.Moved));
        Assert.Equal([junk], set.RemovedNeighbourHoldIds);
        Assert.Equal(2, quick.OverlapsLeftOut);
    }

    private BigUpdateSession Session(List<CarryoverProposal> carry, List<Guid> carried, List<NeighbourOverlap> neighbours) =>
        new(Guid.NewGuid(), centre, carry, [], [], neighbours, CarriedOldHoldIds: carried);

    private static Dictionary<Guid, IReadOnlyList<Guid>> Holds(params (Guid Panel, Guid[] Holds)[] panels) =>
        panels.ToDictionary(p => p.Panel, p => (IReadOnlyList<Guid>)p.Holds);
}
