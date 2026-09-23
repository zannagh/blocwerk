// <copyright file="CarryoverPaneOldHoldScopeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the carryover review's "before" pane old-hold set. The pane draws the old holds over the
/// LIVE CENTRE panel photo, and hold coordinates are PANEL-normalized, so only centre-panel holds have
/// meaningful coordinates there. The UI used to re-derive its own set with a WALL-WIDE
/// <c>GetHoldsForGenerationAsync(wallId, generation)</c>, which re-projected every side-panel hold onto
/// the centre image as a phantom circle over the mats — and a Removed verdict on one of those phantoms
/// froze the real boulders that used it (<c>FreezeRemovedBouldersAsync</c> runs before the
/// old-hold lookup guard). The session now hands the pane the matcher's own set instead, PER PANEL:
/// <see cref="BigUpdateSession.CarriedPanels"/> (drawn, paired with the panel photo it belongs to) and
/// <see cref="BigUpdateSession.CarriedOldHoldIds"/> (decided, all re-photographed panels), so the review
/// can never drift from the matcher and the promote again.
/// </summary>
public class CarryoverPaneOldHoldScopeTests
{
    // The set the review DRAWS (the centre panel's) must contain ONLY that panel's old holds: no
    // co-updated NEIGHBOUR hold, no hold on a panel that was not re-shot at all, and no crash-mat false
    // positive. NON-VACUOUS: the wall-wide read this replaces returned all four categories.
    [Fact]
    public async Task CarriedPanels_CentrePanelSet_ExcludesNeighbourAndNonUpdatedAndMatHolds()
    {
        using var h = new WallTestHarness();
        var w = await SeedWallAsync(h);
        await StageCentrePlusNeighbourAsync(h, w.WallId);

        var session = await BuildService(h).ResumeAsync(w.WallId);
        var centre = session.CarriedPanels!.Single(p => p is { Col: 0, Row: 0 });
        var centreIds = centre.OldHolds.Select(x => x.Id).ToHashSet();

        // Exactly the real centre holds — the mat false positive on the centre is gone too.
        Assert.Equal(w.CentreHoldIds.Count, centreIds.Count);
        Assert.True(w.CentreHoldIds.All(centreIds.Contains));
        Assert.DoesNotContain(w.MatHoldId, centreIds);

        // A co-updated neighbour's hold and a hold on the panel that was not re-shot are both out.
        Assert.DoesNotContain(w.NeighbourHoldId, centreIds);
        Assert.DoesNotContain(w.FarHoldId, centreIds);

        // The neighbour's own carried set is present and holds its hold — cross-generation carryover is
        // per panel, so the set exists for every re-shot panel even though the review draws one at a time.
        var neighbour = session.CarriedPanels!.Single(p => p is { Col: 1, Row: 0 });
        Assert.Equal([w.NeighbourHoldId], neighbour.OldHolds.Select(x => x.Id).ToList());

        // The panel that was NOT re-photographed has no carried set at all.
        Assert.DoesNotContain(session.CarriedPanels!, p => p is { Col: 2, Row: 0 });
    }

    // The DECIDED set is deliberately wider than the drawn one: it spans every updated panel, exactly as
    // the promote's carry scope does. A co-updated neighbour hold must stay in it (or the promote's
    // undecided-reconcile would carry it twin-less and clone a duplicate next to its staged detection),
    // while the non-updated panel's hold and the mat false positive must stay out.
    [Fact]
    public async Task CarriedOldHoldIds_SpanUpdatedPanels_ButNotTheNonUpdatedPanelOrMats()
    {
        using var h = new WallTestHarness();
        var w = await SeedWallAsync(h);
        await StageCentrePlusNeighbourAsync(h, w.WallId);

        var session = await BuildService(h).ResumeAsync(w.WallId);
        var carried = session.CarriedOldHoldIds!.ToHashSet();

        Assert.Contains(w.NeighbourHoldId, carried);
        Assert.True(w.CentreHoldIds.All(carried.Contains));
        Assert.DoesNotContain(w.FarHoldId, carried);
        Assert.DoesNotContain(w.MatHoldId, carried);

        // Every drawable set is a subset of the decided one — no hold can be drawn without a decision.
        Assert.True(session.CarriedPanels!.SelectMany(p => p.OldHolds).All(x => carried.Contains(x.Id)));
    }

    private static WallBigUpdateService BuildService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            new IndexAlignedMatcher(),
            NullLogger<WallBigUpdateService>.Instance);

    // Live gen-2 wall: centre (0,0) with 33 real holds plus one crash-mat false positive, a neighbour
    // (1,0) with one hold, and a far (2,0) panel that this update does NOT re-shoot. The carried
    // population is comfortably over MatFalseDetectionFilter.MinimumPopulationSize, and large enough that
    // its nearest-rank 97th Y percentile falls on a real hold rather than on the mat itself.
    private static async Task<ScopeWall> SeedWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);

        var wall = new Wall
        {
            Name = "Attic", OwnerId = h.Owner.Id, CurrentGeneration = 2,
            Photo = [1, 2, 3], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var centre = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 2 };
        var neighbour = new WallPanel { WallId = wall.Id, Col = 1, Row = 0, Photo = [2], PhotoContentType = "image/jpeg", Generation = 2 };
        var far = new WallPanel { WallId = wall.Id, Col = 2, Row = 0, Photo = [3], PhotoContentType = "image/jpeg", Generation = 2 };
        db.WallPanels.AddRange(centre, neighbour, far);

        // The real centre holds, evenly spread over the upper two thirds of the panel. Their radii vary
        // slightly on purpose: the mat classifier is population-relative and keeps everything when the
        // radius spread is degenerate (MAD 0), which would make the mat assertion below vacuous.
        var centreHolds = new List<Hold>();
        for (var i = 0; i < 33; i++)
        {
            centreHolds.Add(new Hold
            {
                WallId = wall.Id, WallPanelId = centre.Id,
                X = 0.10 + (0.025 * i), Y = 0.10 + (0.02 * i),
                Radius = 0.016 + (0.001 * (i % 5)), Generation = 2,
            });
        }

        // The crash-mat signature the filter is built to reject: a radius outlier in the bottom margin.
        var mat = new Hold
        {
            WallId = wall.Id, WallPanelId = centre.Id, X = 0.50, Y = 0.97, Radius = 0.12, Generation = 2,
        };
        var neighbourHold = new Hold { WallId = wall.Id, WallPanelId = neighbour.Id, X = 0.55, Y = 0.40, Radius = 0.02, Generation = 2 };
        var farHold = new Hold { WallId = wall.Id, WallPanelId = far.Id, X = 0.80, Y = 0.50, Radius = 0.02, Generation = 2 };

        db.Holds.AddRange(centreHolds);
        db.Holds.AddRange(mat, neighbourHold, farHold);
        await db.SaveChangesAsync();

        return new ScopeWall(
            wall.Id,
            centreHolds.Select(x => x.Id).ToList(),
            mat.Id,
            neighbourHold.Id,
            farHold.Id);
    }

    // Stages a centre + (1,0) neighbour update at gen 3, each panel with one staged detection. The (2,0)
    // far panel is NOT re-shot, so its holds are outside the update's carry scope entirely.
    private static async Task StageCentrePlusNeighbourAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();

        var centrePanel = new WallPanel
        {
            WallId = wallId, Col = 0, Row = 0, Photo = null,
            StagedPhoto = [7], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        var neighbourPanel = new WallPanel
        {
            WallId = wallId, Col = 1, Row = 0, Photo = null,
            StagedPhoto = [8], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        db.WallPanels.AddRange(centrePanel, neighbourPanel);

        db.Holds.AddRange(
            new Hold
            {
                WallId = wallId, WallPanelId = centrePanel.Id, X = 0.11, Y = 0.11, Radius = 0.02,
                Generation = 3, IsAutoDetected = true, NeedsReview = true,
            },
            new Hold
            {
                WallId = wallId, WallPanelId = neighbourPanel.Id, X = 0.56, Y = 0.41, Radius = 0.02,
                Generation = 3, IsAutoDetected = true, NeedsReview = true,
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A deterministic stand-in for the OpenCV matcher: proposes left[i] ↔ right[i] for the overlapping
    /// prefix and leaves the rest unmatched, so the session build runs end to end without native OpenCV.
    /// </summary>
    private sealed class IndexAlignedMatcher : IHoldOverlapMatcher
    {
        public HoldOverlapResult Match(
            byte[] leftImage,
            IReadOnlyList<MatcherHold> leftHolds,
            byte[] rightImage,
            IReadOnlyList<MatcherHold> rightHolds,
            HoldOverlapDirection direction,
            ILogger? diag = null,
            HoldOverlapSeed? seed = null)
        {
            var n = Math.Min(leftHolds.Count, rightHolds.Count);
            var proposals = new List<HoldOverlapProposal>();
            for (var i = 0; i < n; i++)
            {
                proposals.Add(new HoldOverlapProposal(leftHolds[i].Id, rightHolds[i].Id, 0.9, false, 1.0, null));
            }

            return new HoldOverlapResult(
                proposals,
                leftHolds.Skip(n).Select(hold => hold.Id).ToList(),
                rightHolds.Skip(n).Select(hold => hold.Id).ToList());
        }
    }

    private sealed record ScopeWall(
        Guid WallId,
        List<Guid> CentreHoldIds,
        Guid MatHoldId,
        Guid NeighbourHoldId,
        Guid FarHoldId);
}
