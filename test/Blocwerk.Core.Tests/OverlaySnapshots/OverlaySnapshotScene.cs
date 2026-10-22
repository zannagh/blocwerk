// <copyright file="OverlaySnapshotScene.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.OverlaySnapshots;

/// <summary>
/// The wall every overlay snapshot draws: two live panels (the centre one at the wall's generation, a right one a
/// generation behind), and on the centre panel one hold of every kind the overlay draws differently. Ids are fixed so
/// the rendered markup is the same on every run; only the wall id is generated, and the renderer masks it.
/// </summary>
internal sealed class OverlaySnapshotScene : IDisposable
{
    public static readonly Guid CentrePanel = Id(0xC0);
    public static readonly Guid RightPanel = Id(0xC1);
    public static readonly Guid Circle = Id(1);
    public static readonly Guid Foot = Id(2);
    public static readonly Guid Polygon = Id(3);
    public static readonly Guid Pocket = Id(4);
    public static readonly Guid VirtualOnPanel = Id(5);
    public static readonly Guid Kept = Id(6);
    public static readonly Guid Stacked = Id(7);
    public static readonly Guid Uncoloured = Id(8);
    public static readonly Guid OldGeneration = Id(9);
    public static readonly Guid VirtualNoPanel = Id(10);
    public static readonly Guid RightHold = Id(11);
    public static readonly Guid ShapedFoot = Id(12);

    private const int Generation = 2;

    private OverlaySnapshotScene(WallTestHarness harness, WallPanelService panels)
    {
        Harness = harness;
        Panels = panels;
    }

    public WallTestHarness Harness { get; }

    public WallPanelService Panels { get; }

    public Guid WallId => Harness.WallId;

    public static async Task<OverlaySnapshotScene> CreateAsync()
    {
        var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0, generation: Generation);
        await SeedAsync(harness);
        var panels = new WallPanelService(
            harness.DbContextFactory, harness.CurrentUser, harness.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);
        return new OverlaySnapshotScene(harness, panels);
    }

    /// <summary>The live panels, as the wall and boulder pages list them.</summary>
    public async Task<IReadOnlyList<WallPanelInfo>> LivePanelsAsync() =>
        (await Panels.GetPanelsAsync(WallId)).Where(p => p.IsLive).ToList();

    /// <summary>A panel's holds through the wall page's read: its own generation only, virtual holds without a panel left out.</summary>
    public Task<IReadOnlyList<PanelHold>> PanelHoldsAsync(Guid panelId) => Panels.GetPanelHoldsAsync(WallId, panelId, includeStaged: false);

    /// <summary>The holds as they are stored, for the boulder view's own grouping.</summary>
    public async Task<List<Hold>> HoldsAsync()
    {
        await using var db = Harness.CreateContext();
        return [.. db.Holds.Where(h => h.WallId == WallId).OrderBy(h => h.Id)];
    }

    public void Dispose() => Harness.Dispose();

    private static async Task SeedAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        db.WallPanels.Add(new WallPanel { Id = CentrePanel, WallId = harness.WallId, Col = 0, Row = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg", Generation = Generation });
        db.WallPanels.Add(new WallPanel { Id = RightPanel, WallId = harness.WallId, Col = 1, Row = 0, Photo = [4, 5, 6], PhotoContentType = "image/jpeg", Generation = Generation - 1 });
        foreach (var hold in Holds(harness.WallId))
        {
            db.Holds.Add(hold);
        }

        await db.SaveChangesAsync();
    }

    private static IEnumerable<Hold> Holds(Guid wallId)
    {
        Hold On(Guid id, Guid? panel, double x, double y, int generation = Generation) =>
            new() { Id = id, WallId = wallId, WallPanelId = panel, X = x, Y = y, Radius = 0.021, Generation = generation };

        yield return With(On(Circle, CentrePanel, 0.1234, 0.2345), color: "red");
        yield return With(On(Foot, CentrePanel, 0.3, 0.7), color: "yellow", foot: true);
        yield return With(On(Polygon, CentrePanel, 0.45, 0.35), color: "blue", shape: Octagon(0.03));
        yield return With(On(Pocket, CentrePanel, 0.6, 0.5), color: "green", shape: Octagon(0.04), holes: [Octagon(0.012)]);
        var virtualHold = With(On(VirtualOnPanel, CentrePanel, 0.8, 0.1), color: "purple");
        virtualHold.IsVirtual = true;
        yield return virtualHold;
        var kept = With(On(Kept, CentrePanel, 0.7, 0.8), color: "orange", shape: Octagon(0.02));
        kept.KeptUnconfirmedGeneration = Generation;
        yield return kept;
        var stacked = With(On(Stacked, CentrePanel, 0.125, 0.235), color: "white");
        stacked.StackedOnHoldId = Circle;
        yield return stacked;
        yield return On(Uncoloured, CentrePanel, 0.9, 0.9);
        yield return With(On(OldGeneration, CentrePanel, 0.5, 0.5, Generation - 1), color: "black");
        var virtualNoPanel = With(On(VirtualNoPanel, null, 0.5, 0.05), color: "pink");
        virtualNoPanel.IsVirtual = true;
        yield return virtualNoPanel;
        yield return With(On(RightHold, RightPanel, 0.4, 0.4, Generation - 1), color: "salmon");
        yield return With(On(ShapedFoot, CentrePanel, 0.2, 0.9), color: "gray", foot: true, shape: Octagon(0.015));
    }

    private static Hold With(Hold hold, string color, bool foot = false, List<ShapePoint>? shape = null, List<List<ShapePoint>>? holes = null)
    {
        hold.Color = color;
        hold.Category = foot ? HoldCategory.Foot : HoldCategory.Hand;
        hold.ShapePoints = shape;
        hold.ShapeHoles = holes;
        return hold;
    }

    // An irregular octagon, so rounding and the two-decimal formatting both show in the markup.
    private static List<ShapePoint> Octagon(double radius) =>
        [.. ShapePoint.DefaultOctagon(radius).Select((p, i) => new ShapePoint { Dx = p.Dx * (1 + (0.07 * (i % 3))), Dy = p.Dy * (1 - (0.05 * (i % 2))) })];

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");
}
