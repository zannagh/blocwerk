using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The Revise editor's off-screen accounting: which panel a selected hold is on, what the shown panel hides, and
/// which selections were never on screen (those hold the save back, online and offline).
/// </summary>
public class RevisePanelTrackerTests
{
    private static readonly WallPanelInfo Centre = new(Guid.NewGuid(), 0, 0, true, false, 6, false);
    private static readonly WallPanelInfo Right = new(Guid.NewGuid(), 1, 0, true, false, 6, false);

    [Fact]
    public void VirtualHoldAddedOnTheRightPanelWithoutAPanelId_CountsOnTheRightPanel_NotTheCentre()
    {
        // While a wall is staged a new virtual hold is stored with no panel id; it was placed on the right panel.
        var tracker = new RevisePanelTracker([], [Centre, Right]);
        var added = new Hold { IsVirtual = true, WallPanelId = null };
        tracker.AddHold(added, Right.Id);
        tracker.MarkSeen(Right.Id);

        Assert.Equal(Right.Id, tracker.PanelOf(added.Id));
        Assert.Empty(tracker.OffScreen([added.Id], Right.Id));
        var hidden = Assert.Single(tracker.OffScreen([added.Id], Centre.Id));
        Assert.Equal((Right, 1), hidden);
        Assert.Null(tracker.FirstUnseen([added.Id]));
    }

    [Fact]
    public void HoldAddedWithAPanelId_KeepsItsOwnPanel()
    {
        var tracker = new RevisePanelTracker([], [Centre, Right]);
        var added = new Hold { IsVirtual = true, WallPanelId = Right.Id };
        tracker.AddHold(added, Centre.Id);

        Assert.Equal(Right.Id, tracker.PanelOf(added.Id));
    }

    [Fact]
    public void HoldWithNoPanel_DrawsOnTheCentre()
    {
        var legacy = new Hold { WallPanelId = null };
        var tracker = new RevisePanelTracker([legacy], [Centre, Right]);

        Assert.Equal(Centre.Id, tracker.PanelOf(legacy.Id));
    }

    [Fact]
    public void SelectionOnAPanelNeverShown_IsUnseen_UntilThatPanelIsShown()
    {
        var onCentre = new Hold { WallPanelId = Centre.Id };
        var onRight = new Hold { WallPanelId = Right.Id };
        var tracker = new RevisePanelTracker([onCentre, onRight], [Centre, Right]);
        tracker.MarkSeen(Centre.Id);

        Assert.Equal((Right, 1), tracker.FirstUnseen([onCentre.Id, onRight.Id]));

        tracker.MarkSeen(Right.Id);
        Assert.Null(tracker.FirstUnseen([onCentre.Id, onRight.Id]));
    }

    [Fact]
    public void SelectionWithATwinOnTheShownPanel_IsNotOffScreen()
    {
        var onRight = new Hold { WallPanelId = Right.Id };
        var twinOnCentre = new Hold { WallPanelId = Centre.Id };
        var tracker = new RevisePanelTracker([onRight, twinOnCentre], [Centre, Right])
        {
            Twins = new Dictionary<Guid, IReadOnlyList<Guid>> { [onRight.Id] = [twinOnCentre.Id] },
        };
        tracker.MarkSeen(Centre.Id);

        Assert.Empty(tracker.OffScreen([onRight.Id], Centre.Id));
        Assert.Null(tracker.FirstUnseen([onRight.Id]));
    }
}
