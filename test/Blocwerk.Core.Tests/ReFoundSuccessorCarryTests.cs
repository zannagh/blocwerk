// <copyright file="ReFoundSuccessorCarryTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.PanelUpdateCarryFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What a re-found ("twin") successor keeps when the promote makes a fresh detection the next row of an old
/// hold: the neighbour removal path must not delete it, it must inherit the old hold's 3D placement and
/// measurements and its hand-drawn outline status, and a merge must not lose the second hold's provenance.
/// </summary>
public class ReFoundSuccessorCarryTests
{
    // F4: a removal on the neighbour panel (a suggested discard) can name a detection the carry consumed as a twin.
    // NON-VACUOUS: before the guard the promote deleted the twin it had just repointed the Span boulder onto,
    // and SaveChanges failed on the severed membership — the update could not be applied at all.
    [Fact]
    public async Task NeighbourRemoval_OfAReFoundTwin_KeepsTheTwin()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        var s = await StageAsync(h, w.WallId, 3, 0, 1);

        var confirmation = new BigUpdateConfirmation(
            [
                new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId),
                new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, s[1].HoldId),
            ],
            [],
            [],
            [new NeighbourLinkSet(s[1].PanelId, [], [s[1].HoldId])]);
        await Service(h).PromoteAsync(w.WallId, confirmation);

        await using var db = h.CreateContext();
        var twin = await db.Holds.SingleAsync(x => x.Id == s[1].HoldId);
        Assert.Equal(3, twin.Generation);

        // A removal on the step's own panel is a suggested discard, not the user's "Delete hold": no override flag.
        Assert.False(twin.NeedsReview);
        Assert.Contains(s[1].HoldId, await db.BoulderHolds.Where(bh => bh.BoulderId == w.SpanBoulderId).Select(bh => bh.HoldId).ToListAsync());
        Assert.Equal(s[1].HoldId, (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.NeighbourHoldId)).NewHoldId);
    }

    // F6: a twin used to take only name, colour and category. Now it keeps the old hold's facet position,
    // sizes, footprint, protrusion and volume placement (where the new photo measured nothing), and a
    // hand-drawn outline warped onto it stays labelled Manual. A twin's OWN measurement still wins.
    [Fact]
    public async Task Twin_InheritsPlacementAndMeasurements_AndKeepsHandDrawnOutlineStatus()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        await MutateAsync(h, w.CentreHoldId, Placed);
        await MutateAsync(h, w.NeighbourHoldId, Placed);
        var s = await StageAsync(h, w.WallId, 3, 0, 1);
        await MutateAsync(h, s[0].HoldId, x => x.OutlineSource = HoldOutlineSource.AutoCircle);
        await MutateAsync(h, s[1].HoldId, x => (x.FacetId, x.PlaneAMm, x.PlaneBMm) = ("9", 1, 2));

        await Service(h).PromoteAsync(w.WallId, new BigUpdateConfirmation(
            [
                new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId),
                new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, s[1].HoldId),
            ],
            [],
            [],
            [],
            CarriedWarpShapes: new Dictionary<Guid, IReadOnlyList<HoldPositionNorm>>
            {
                [w.CentreHoldId] = [new(0.30, 0.40), new(0.32, 0.40), new(0.32, 0.42), new(0.30, 0.42)],
            }));

        await using var db = h.CreateContext();
        var centre = await db.Holds.SingleAsync(x => x.Id == s[0].HoldId);
        Assert.Equal(("3", 120.0, 340.0), (centre.FacetId, centre.PlaneAMm, centre.PlaneBMm));
        Assert.Equal("texture-registration", centre.MetricSource);
        Assert.Equal((50.0, 40.0, 1500.0), (centre.WidthMm, centre.HeightMm, centre.AreaMm2));
        Assert.Equal(("{\"f\":1}", "{\"p\":1}", "{\"v\":1}"), (centre.FootprintMm, centre.ProtrusionMm, centre.VolumePlacementJson));
        Assert.Equal(HoldOutlineSource.Manual, centre.OutlineSource);
        Assert.Equal(4, centre.ShapePoints!.Count);

        var neighbour = await db.Holds.SingleAsync(x => x.Id == s[1].HoldId);
        Assert.Equal(("9", 1.0, 2.0), (neighbour.FacetId, neighbour.PlaneAMm, neighbour.PlaneBMm));
        Assert.Equal(50.0, neighbour.WidthMm);
    }

    // A "changed" twin (a different hold, or an accepted "this hold moved" relocation) sits somewhere the old
    // 3D placement does not describe: it inherits neither the old position nor the old measurements.
    [Fact]
    public async Task ChangedTwin_InheritsNoPlacementOrMeasurements()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        await MutateAsync(h, w.CentreHoldId, Placed);
        var s = await StageAsync(h, w.WallId, 3, 0);

        await Service(h).PromoteAsync(w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Changed, s[0].HoldId)));

        await using var db = h.CreateContext();
        var twin = await db.Holds.SingleAsync(x => x.Id == s[0].HoldId);
        Assert.Null(twin.FacetId);
        Assert.Null(twin.PlaneAMm);
        Assert.Null(twin.MetricSource);
        Assert.Null(twin.VolumePlacementJson);
        Assert.Null(twin.WidthMm);
        Assert.Null(twin.FootprintMm);
        Assert.Null(twin.ProtrusionMm);
    }

    // A twin away from where the matcher predicts the old hold landed is not on the old 3D spot: the old
    // position is not copied (sizes are, it is still the same hold). A twin the marker pass sized in its own
    // marker square keeps that metric source when the old position is copied.
    [Fact]
    public async Task Twin_OffTheWarpedSpot_KeepsNoOldPosition_AndLocalMarkerSourceSurvives()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        await MutateAsync(h, w.CentreHoldId, Placed);
        await MutateAsync(h, w.NeighbourHoldId, Placed);
        var s = await StageAsync(h, w.WallId, 3, 0, 1);
        await MutateAsync(h, s[1].HoldId, x => (x.WidthMm, x.MetricSource) = (33, "local-marker"));

        await Service(h).PromoteAsync(w.WallId, new BigUpdateConfirmation(
            [
                new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId),
                new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, s[1].HoldId),
            ],
            [],
            [],
            [],
            new Dictionary<Guid, HoldPositionNorm>
            {
                [w.CentreHoldId] = new(0.70, 0.80),
                [w.NeighbourHoldId] = new(0.56, 0.41),
            }));

        await using var db = h.CreateContext();
        var off = await db.Holds.SingleAsync(x => x.Id == s[0].HoldId);
        Assert.Null(off.FacetId);
        Assert.Null(off.VolumePlacementJson);
        Assert.True(off.NeedsReview);
        Assert.Equal(50.0, off.WidthMm);

        var local = await db.Holds.SingleAsync(x => x.Id == s[1].HoldId);
        Assert.Equal("3", local.FacetId);
        Assert.Equal("local-marker", local.MetricSource);
        Assert.Equal(33.0, local.WidthMm);
    }

    // F10: two old holds merged onto one twin. The second (hand-added, flagged for review, named) used to
    // lose all of that to the first writer; now the merged hold is hand-added, needs review, and is named.
    [Fact]
    public async Task Merge_KeepsTheSecondHoldsProvenanceAndReviewFlag()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h);
        await MutateAsync(h, w.CentreHoldId, x => (x.IsAutoDetected, x.NeedsReview) = (true, false));
        Guid handAdded;
        await using (var db = h.CreateContext())
        {
            var hold = new Hold
            {
                WallId = w.WallId, WallPanelId = w.PanelIds[0], X = 0.33, Y = 0.40, Radius = 0.02, Generation = 2,
                IsAutoDetected = false, NeedsReview = true, Name = "Crimp",
            };
            db.Holds.Add(hold);
            await db.SaveChangesAsync();
            handAdded = hold.Id;
        }

        var s = await StageAsync(h, w.WallId, 3, 0);
        await Service(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId),
            new CarryoverDecision(handAdded, CarryKind.Carried, s[0].HoldId)));

        await using var read = h.CreateContext();
        var twin = await read.Holds.SingleAsync(x => x.Id == s[0].HoldId);
        Assert.False(twin.IsAutoDetected);
        Assert.True(twin.NeedsReview);
        Assert.Equal("Crimp", twin.Name);
        Assert.Equal(2, await read.HoldGenerationLinks.CountAsync(l => l.NewHoldId == s[0].HoldId));
    }

    private static void Placed(Hold x)
    {
        (x.FacetId, x.PlaneAMm, x.PlaneBMm, x.MetricSource) = ("3", 120, 340, "texture-registration");
        (x.WidthMm, x.HeightMm, x.AreaMm2) = (50, 40, 1500);
        (x.FootprintMm, x.ProtrusionMm, x.VolumePlacementJson) = ("{\"f\":1}", "{\"p\":1}", "{\"v\":1}");
        x.OutlineSource = HoldOutlineSource.Manual;
        x.ShapePoints = [new() { Dx = -0.01, Dy = 0 }, new() { Dx = 0, Dy = -0.01 }, new() { Dx = 0.01, Dy = 0 }];
    }

    private static async Task MutateAsync(WallTestHarness h, Guid holdId, Action<Hold> change)
    {
        await using var db = h.CreateContext();
        change(await db.Holds.SingleAsync(x => x.Id == holdId));
        await db.SaveChangesAsync();
    }
}
