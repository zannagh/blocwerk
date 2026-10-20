using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The Revise editor's pre-selection for a frozen boulder (holds still at an older generation): it must tick the
/// holds the lineage says those holds became, the same set the boulder page's Then view draws, and never a
/// raw-X/Y neighbour, least of all one on another panel the editor does not show.
/// </summary>
public class RevisePrefillMapperTests
{
    private static readonly Guid OldCentre = Guid.Parse("00000000-0000-0000-0000-00000000a500");
    private static readonly Guid OldRight = Guid.Parse("00000000-0000-0000-0000-00000000a510");
    private static readonly Guid LiveCentre = Guid.Parse("00000000-0000-0000-0000-00000000a600");
    private static readonly Guid LiveRight = Guid.Parse("00000000-0000-0000-0000-00000000a610");

    private static readonly Dictionary<Guid, (int Col, int Row)> Positions = new()
    {
        [OldCentre] = (0, 0),
        [OldRight] = (1, 0),
        [LiveCentre] = (0, 0),
        [LiveRight] = (1, 0),
    };

    private static readonly Guid[] LivePanels = [LiveCentre, LiveRight];

    [Fact]
    public void FrozenBoulder_TicksItsLineageSuccessors_NotTheNearerNeighbour()
    {
        // The prod shape: the gen-6 photo is shifted, so the hold's successor sits 0.02 away while an unrelated
        // gen-6 hold sits right on the old coordinates. Lineage must win.
        var old = OldHold(OldCentre, 0.40, 0.50);
        var successor = LiveHold(LiveCentre, 0.42, 0.52);
        var neighbour = LiveHold(LiveCentre, 0.40, 0.50);
        var other = OldHold(OldCentre, 0.90, 0.90);
        var neighbourAncestor = Link(other.Id, neighbour.Id);

        var result = Map(
            [Mark(old, HoldType.Start, HoldUsage.FootOnly)],
            [Link(old.Id, successor.Id), neighbourAncestor],
            [successor, neighbour]);

        var pick = Assert.Single(result.Picks);
        Assert.Equal(successor.Id, pick.HoldId);
        Assert.Equal(HoldType.Start, pick.Type);
        Assert.Equal(HoldUsage.FootOnly, pick.Usage);
        Assert.Equal(RevisePickSource.Lineage, pick.Source);
        Assert.Empty(result.UnmappedHoldIds);
    }

    [Fact]
    public void MultiGenerationChain_IsWalkedToTheLiveRow()
    {
        // gen 3 -> 4 -> 5 -> 6 (Batman on The Attic): every hop has to be followed.
        var g3 = OldHold(OldCentre, 0.3, 0.3, generation: 3);
        var g4 = OldHold(OldCentre, 0.3, 0.3, generation: 4);
        var g5 = OldHold(OldCentre, 0.3, 0.3, generation: 5);
        var g6 = LiveHold(LiveCentre, 0.31, 0.29);

        var result = Map(
            [Mark(g3, HoldType.Top)],
            [Link(g5.Id, g6.Id, 5, 6), Link(g3.Id, g4.Id, 3, 4), Link(g4.Id, g5.Id, 4, 5)],
            [g6]);

        Assert.Equal(g6.Id, Assert.Single(result.Picks).HoldId);
        Assert.Equal(HoldType.Top, result.Picks[0].Type);
    }

    [Fact]
    public void DeletedSuccessor_LeavesTheHoldUnmapped_AndGuessesNothing()
    {
        // Gummy bear's b4fc1ef4: the gen-6 successor was deleted (tombstone link, NewHoldId null). The old
        // editor ticked the nearest yellow 0.016 away; the hold is gone, so nothing may be ticked for it.
        var old = OldHold(OldCentre, 0.669, 0.264);
        var nearby = LiveHold(LiveCentre, 0.665, 0.266);

        var result = Map([Mark(old)], [new ReviseLineageStep(old.Id, null, HoldGenerationLinkKind.Same, 5, 6)], [nearby]);

        Assert.Empty(result.Picks);
        Assert.Equal(old.Id, Assert.Single(result.UnmappedHoldIds));
    }

    [Fact]
    public void BoulderOnTheRightPanel_OpensOnTheRightPanel_AndNeverTicksTheCentreLookalike()
    {
        // Panel-local coordinates: a centre-panel hold at the same X/Y is a different place on the wall.
        var oldA = OldHold(OldRight, 0.2, 0.2);
        var oldB = OldHold(OldRight, 0.6, 0.6);
        var newA = LiveHold(LiveRight, 0.21, 0.2);
        var newB = LiveHold(LiveRight, 0.61, 0.6);
        var centreLookalike = LiveHold(LiveCentre, 0.2, 0.2);

        var result = Map([Mark(oldA), Mark(oldB)], [Link(oldA.Id, newA.Id), Link(oldB.Id, newB.Id)], [newA, newB, centreLookalike]);

        Assert.Equal(new[] { newA.Id, newB.Id }.Order(), result.Picks.Select(p => p.HoldId).Order());
        Assert.Equal(LiveRight, result.StartPanelId);
    }

    [Fact]
    public void StartPanel_IsThePanelWithMostMappedHolds()
    {
        var holds = Enumerable.Range(0, 3).Select(i => (Old: OldHold(OldCentre, 0.1 * i, 0.5), New: LiveHold(LiveCentre, 0.1 * i, 0.5))).ToList();
        var right = (Old: OldHold(OldCentre, 0.9, 0.5), New: LiveHold(LiveRight, 0.1, 0.5));
        holds.Add(right);

        var result = Map(holds.Select(h => Mark(h.Old)).ToList(), holds.Select(h => Link(h.Old.Id, h.New.Id)).ToList(), holds.Select(h => h.New).ToList());

        Assert.Equal(4, result.Picks.Count);
        Assert.Equal(LiveCentre, result.StartPanelId);
    }

    [Fact]
    public void ChangedLink_StillTicksTheSuccessor_ButFlagsIt()
    {
        var old = OldHold(OldCentre, 0.5, 0.5);
        var successor = LiveHold(LiveCentre, 0.55, 0.5);

        var result = Map([Mark(old)], [Link(old.Id, successor.Id, kind: HoldGenerationLinkKind.Changed)], [successor]);

        Assert.Equal(RevisePickSource.LineageChanged, Assert.Single(result.Picks).Source);
    }

    [Fact]
    public void ChangedAndSamePathsToOneRow_CountAsSame()
    {
        var old = OldHold(OldCentre, 0.5, 0.5);
        var mid = OldHold(OldCentre, 0.5, 0.5, generation: 5);
        var successor = LiveHold(LiveCentre, 0.5, 0.5);

        var result = Map(
            [Mark(old)],
            [Link(old.Id, successor.Id, 4, 6, HoldGenerationLinkKind.Changed), Link(old.Id, mid.Id, 4, 5), Link(mid.Id, successor.Id, 5, 6)],
            [successor]);

        Assert.Equal(RevisePickSource.Lineage, Assert.Single(result.Picks).Source);
    }

    [Fact]
    public void MergedHolds_ConvergeOnOnePick_TheTopMarkWins()
    {
        var a = OldHold(OldCentre, 0.5, 0.5);
        var b = OldHold(OldCentre, 0.52, 0.5);
        var merged = LiveHold(LiveCentre, 0.51, 0.5);

        var result = Map([Mark(a, HoldType.Normal), Mark(b, HoldType.Top)], [Link(a.Id, merged.Id), Link(b.Id, merged.Id)], [merged]);

        var pick = Assert.Single(result.Picks);
        Assert.Equal(HoldType.Top, pick.Type);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), pick.SourceHoldIds.Order());
        Assert.Empty(result.UnmappedHoldIds);
    }

    [Fact]
    public void SplitHold_TicksEverySuccessor()
    {
        var old = OldHold(OldCentre, 0.5, 0.5);
        var left = LiveHold(LiveCentre, 0.49, 0.5);
        var right = LiveHold(LiveCentre, 0.51, 0.5);

        var result = Map([Mark(old)], [Link(old.Id, left.Id), Link(old.Id, right.Id)], [left, right]);

        Assert.Equal(2, result.Picks.Count);
    }

    [Fact]
    public void CarriedHold_IsKeptAsIs()
    {
        var carried = LiveHold(LiveCentre, 0.5, 0.5);

        var result = Map([new ReviseMark(carried.Id, HoldType.Start, HoldUsage.HandAndFoot, LiveCentre, 0.5, 0.5)], [], [carried]);

        Assert.Equal(RevisePickSource.Carried, Assert.Single(result.Picks).Source);
    }

    [Fact]
    public void HoldWithoutLineage_IsGuessedOnTheSamePanelPosition_Only()
    {
        // A virtual hold with no panel id draws on the centre photo, so its guess is made among centre-panel holds
        // (and null-panel ones); the right panel's hold at the same X/Y is out of bounds.
        var oldVirtual = new Hold { X = 0.30, Y = 0.30, Generation = 5, IsVirtual = true };
        var onRight = LiveHold(LiveRight, 0.30, 0.30);
        var liveVirtual = new Hold { X = 0.31, Y = 0.305, Generation = 6, IsVirtual = true };

        var result = Map([Mark(oldVirtual)], [], [onRight, liveVirtual]);

        var pick = Assert.Single(result.Picks);
        Assert.Equal(liveVirtual.Id, pick.HoldId);
        Assert.Equal(RevisePickSource.Guessed, pick.Source);
        Assert.Equal(LiveCentre, result.StartPanelId);
    }

    [Fact]
    public void Guess_SkipsHoldsTheLineageClaims_AndHoldsBeyondTheLimit()
    {
        var unlinked = OldHold(OldCentre, 0.5, 0.5);
        var other = OldHold(OldCentre, 0.1, 0.1);
        var claimed = LiveHold(LiveCentre, 0.5, 0.505);
        var tooFar = LiveHold(LiveCentre, 0.5 + RevisePrefillMapper.MaxGuessDistance + 0.001, 0.5);

        var result = Map([Mark(unlinked)], [Link(other.Id, claimed.Id)], [claimed, tooFar]);

        Assert.Empty(result.Picks);
        Assert.Equal(unlinked.Id, Assert.Single(result.UnmappedHoldIds));
    }

    [Fact]
    public void CyclicLinks_DoNotSpin()
    {
        var a = OldHold(OldCentre, 0.5, 0.5, generation: 4);
        var b = OldHold(OldCentre, 0.5, 0.5, generation: 5);

        var result = Map([Mark(a)], [Link(a.Id, b.Id, 4, 5), Link(b.Id, a.Id, 5, 4), Link(b.Id, a.Id, 4, 5)], []);

        Assert.Empty(result.Picks);
        Assert.Single(result.UnmappedHoldIds);
    }

    private static RevisePrefill Map(IReadOnlyCollection<ReviseMark> marks, IReadOnlyCollection<ReviseLineageStep> lineage, IReadOnlyCollection<Hold> live) =>
        RevisePrefillMapper.Map(marks, lineage, live, Positions, LivePanels);

    private static Hold OldHold(Guid panel, double x, double y, int generation = 5) =>
        new() { WallPanelId = panel, X = x, Y = y, Generation = generation };

    private static Hold LiveHold(Guid panel, double x, double y) =>
        new() { WallPanelId = panel, X = x, Y = y, Generation = 6 };

    private static ReviseMark Mark(Hold hold, HoldType type = HoldType.Normal, HoldUsage usage = HoldUsage.HandAndFoot) =>
        new(hold.Id, type, usage, hold.WallPanelId, hold.X, hold.Y);

    private static ReviseLineageStep Link(Guid from, Guid to, int fromGeneration = 5, int toGeneration = 6, HoldGenerationLinkKind kind = HoldGenerationLinkKind.Same) =>
        new(from, to, kind, fromGeneration, toGeneration);
}
