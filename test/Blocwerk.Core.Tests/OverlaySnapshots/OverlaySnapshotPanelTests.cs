// <copyright file="OverlaySnapshotPanelTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests.OverlaySnapshots;

/// <summary>
/// The wall and boulder views' panel overlay (MultiPanelViewer), byte for byte as the Server drew it before the
/// overlay moved into Blocwerk.Shared.Ui. The wall view reads its holds through the real panel-holds query, so the
/// generation gate and the dropped panel-less virtual hold are part of what is compared; the boulder view groups its
/// holds as BoulderDetail does, so the panel-less virtual hold lands on the centre panel.
/// </summary>
public class OverlaySnapshotPanelTests
{
    public static TheoryData<string> WallCases => new() { "wall-on-demand", "wall-all-holds", "wall-kept-shown", "wall-usage", "wall-outdated-panel", "wall-show-all", "wall-heat" };

    public static TheoryData<string> BoulderCases => new() { "boulder-focus", "boulder-default-colours", "boulder-historic", "boulder-context-holds" };

    [Theory]
    [MemberData(nameof(WallCases))]
    public async Task TheWallView_RendersAsBeforeTheMove(string name)
    {
        using var scene = await OverlaySnapshotScene.CreateAsync();
        var parameters = new Dictionary<string, object?>
        {
            [nameof(MultiPanelViewer.WallId)] = scene.WallId,
            [nameof(MultiPanelViewer.Panels)] = await scene.LivePanelsAsync(),
            [nameof(MultiPanelViewer.StartCol)] = name == "wall-outdated-panel" ? 1 : 0,
            [nameof(MultiPanelViewer.StartRow)] = 0,
            [nameof(MultiPanelViewer.ArrowMode)] = MultiPanelViewer.ArrowDisplayMode.AllNeighbours,
            [nameof(MultiPanelViewer.HoldsProvider)] = (Func<Guid, Task<IReadOnlyList<PanelHold>>>)scene.PanelHoldsAsync,
            [nameof(MultiPanelViewer.ShowKeptHolds)] = name == "wall-kept-shown",
            [nameof(MultiPanelViewer.EnableSwipe)] = true,
            [nameof(MultiPanelViewer.OutlinesOnDemand)] = name is not ("wall-all-holds" or "wall-kept-shown"),
            [nameof(MultiPanelViewer.UsageCounts)] = name is "wall-usage" or "wall-heat"
                ? new Dictionary<Guid, int> { [OverlaySnapshotScene.Circle] = 3, [OverlaySnapshotScene.Polygon] = 1 }
                : null,
        };

        // "Show all holds" and the heat map are set by a click; the heat map draws its count badges on the overlay.
        var html = name switch
        {
            "wall-show-all" => await OverlaySnapshotRenderer.RenderInStateAsync<MultiPanelViewer>(parameters, ("showAllHolds", true)),
            "wall-heat" => await OverlaySnapshotRenderer.RenderInStateAsync<MultiPanelViewer>(parameters, ("showAllHolds", true), ("_heatOn", true)),
            _ => await OverlaySnapshotRenderer.RenderAsync<MultiPanelViewer>(parameters),
        };

        OverlaySnapshotRenderer.AssertMatches(name, html, scene.WallId);
    }

    [Theory]
    [MemberData(nameof(BoulderCases))]
    public async Task TheBoulderView_RendersAsBeforeTheMove(string name)
    {
        using var scene = await OverlaySnapshotScene.CreateAsync();
        var live = await scene.LivePanelsAsync();
        var roles = BoulderRoles();
        var holds = (await scene.HoldsAsync()).Where(h => roles.ContainsKey(h.Id)).ToList();
        var byPanel = GroupLikeBoulderDetail(holds, live);
        var parameters = new Dictionary<string, object?>
        {
            [nameof(MultiPanelViewer.WallId)] = scene.WallId,
            [nameof(MultiPanelViewer.Panels)] = live,
            [nameof(MultiPanelViewer.StartCol)] = name == "boulder-historic" ? 1 : 0,
            [nameof(MultiPanelViewer.StartRow)] = 0,
            [nameof(MultiPanelViewer.ArrowMode)] = MultiPanelViewer.ArrowDisplayMode.HoldsOnly,
            [nameof(MultiPanelViewer.HoldsByPanel)] = name == "boulder-context-holds" ? null : byPanel,
            [nameof(MultiPanelViewer.HoldsProvider)] = (Func<Guid, Task<IReadOnlyList<PanelHold>>>)scene.PanelHoldsAsync,
            [nameof(MultiPanelViewer.HighlightedHoldIds)] = roles.Keys.ToHashSet(),
            [nameof(MultiPanelViewer.FootHoldIds)] = roles.Where(r => r.Value.Usage == HoldUsage.FootOnly).Select(r => r.Key).ToHashSet(),
            [nameof(MultiPanelViewer.HoldColor)] = name == "boulder-default-colours" ? null : ColourOf(roles),
            [nameof(MultiPanelViewer.HighlightedPanelIds)] = byPanel.Keys.ToHashSet(),
            [nameof(MultiPanelViewer.EnableSwipe)] = true,
            [nameof(MultiPanelViewer.HistoricMode)] = name == "boulder-historic",
            [nameof(MultiPanelViewer.LensOverlay)] = true,
            [nameof(MultiPanelViewer.BoulderFocus)] = true,
            [nameof(MultiPanelViewer.Fullscreen)] = true,
        };

        var html = await OverlaySnapshotRenderer.RenderAsync<MultiPanelViewer>(parameters);

        OverlaySnapshotRenderer.AssertMatches(name, html, scene.WallId);
    }

    // A boulder over every kind of hold: start, top, a foot-only hold, a hand-only one, a kept one and a virtual top
    // that has no panel id at all.
    private static Dictionary<Guid, (HoldType Type, HoldUsage Usage)> BoulderRoles() => new()
    {
        [OverlaySnapshotScene.Circle] = (HoldType.Start, HoldUsage.HandAndFoot),
        [OverlaySnapshotScene.Foot] = (HoldType.Normal, HoldUsage.FootOnly),
        [OverlaySnapshotScene.Polygon] = (HoldType.Normal, HoldUsage.HandOnly),
        [OverlaySnapshotScene.Pocket] = (HoldType.Normal, HoldUsage.HandAndFoot),
        [OverlaySnapshotScene.Kept] = (HoldType.Normal, HoldUsage.HandAndFoot),
        [OverlaySnapshotScene.Stacked] = (HoldType.Normal, HoldUsage.HandAndFoot),
        [OverlaySnapshotScene.VirtualNoPanel] = (HoldType.Top, HoldUsage.HandAndFoot),
        [OverlaySnapshotScene.RightHold] = (HoldType.Normal, HoldUsage.HandAndFoot),
    };

    private static Func<Guid, (string Fill, string Stroke)> ColourOf(Dictionary<Guid, (HoldType Type, HoldUsage Usage)> roles) =>
        id => roles.TryGetValue(id, out var t)
            ? (BoulderHoldColors.FillFor(t.Type, t.Usage), BoulderHoldColors.Outline(t.Type, t.Usage))
            : (BoulderHoldColors.Fill(HoldType.Normal), BoulderHoldColors.Normal);

    // BoulderDetail.LoadPanelData's grouping: a hold on a live panel stays there, one with no panel id goes to the
    // centre panel, and each panel's holds come bases first.
    private static Dictionary<Guid, IReadOnlyList<PanelHold>> GroupLikeBoulderDetail(List<Hold> holds, IReadOnlyList<WallPanelInfo> live)
    {
        var liveIds = live.Select(p => p.Id).ToHashSet();
        var centre = BoulderGenerationView.CentrePanel(live)?.Id;
        Guid? PanelOf(Hold h) => h.WallPanelId is { } pid ? (liveIds.Contains(pid) ? pid : null) : centre;
        return holds
            .Where(h => PanelOf(h) is not null)
            .GroupBy(h => PanelOf(h)!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PanelHold>)HoldStackOrder.BackToFront(
                    g.Select(h => new PanelHold(
                        h.Id, h.X, h.Y, h.Radius, h.Color, h.Category, h.ShapePoints, h.IsVirtual, h.ShapeHoles,
                        h.KeptUnconfirmedGeneration is not null, h.StackedOnHoldId)),
                    p => p.Id,
                    p => p.StackedOn));
    }
}
