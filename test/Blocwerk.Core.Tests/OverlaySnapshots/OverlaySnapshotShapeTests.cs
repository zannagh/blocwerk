// <copyright file="OverlaySnapshotShapeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Core.Tests.OverlaySnapshots;

/// <summary>
/// The hold renderer every overlay draws through (HoldShape, HoldShine) in each of its looks, and the overlap stepper's
/// panel view (PanelImageView) in its read-only, re-target and editable modes, byte for byte as before the move.
/// </summary>
public class OverlaySnapshotShapeTests
{
    private static readonly Guid Hold = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    public static TheoryData<string> StepperCases => new() { "stepper-plain", "stepper-retarget", "stepper-editable" };

    [Fact]
    public async Task EveryHoldShapeLook_RendersAsBeforeTheMove()
    {
        var html = new StringBuilder();
        foreach (var parameters in ShapeLooks())
        {
            html.Append(await OverlaySnapshotRenderer.RenderAsync<HoldShape>(parameters)).Append('\n');
        }

        OverlaySnapshotRenderer.AssertMatches("hold-shapes", html.ToString(), Guid.Empty);
    }

    [Fact]
    public async Task EveryOutlineLook_RendersAsBeforeTheMove()
    {
        var html = new StringBuilder();
        foreach (var parameters in OutlineLooks())
        {
            html.Append(await OverlaySnapshotRenderer.RenderAsync<HoldShine>(parameters)).Append('\n');
        }

        OverlaySnapshotRenderer.AssertMatches("hold-outlines", html.ToString(), Guid.Empty);
    }

    [Theory]
    [MemberData(nameof(StepperCases))]
    public async Task TheOverlapStepperPanel_RendersAsBeforeTheMove(string name)
    {
        var wallId = Guid.Parse("00000000-0000-0000-0000-0000000000ee");
        var holds = PanelHolds();
        var parameters = new Dictionary<string, object?>
        {
            [nameof(PanelImageView.WallId)] = wallId,
            [nameof(PanelImageView.PhotoUrl)] = $"/api/walls/{wallId}/panels/p/photo",
            [nameof(PanelImageView.Caption)] = "Left panel",
            [nameof(PanelImageView.Holds)] = holds,
            [nameof(PanelImageView.HighlightHoldId)] = holds[0].Id,
            [nameof(PanelImageView.BadgeNumber)] = 4,
            [nameof(PanelImageView.BadgeColor)] = name == "stepper-plain" ? "hsl(210, 80%, 55%)" : "var(--status-info)",
            [nameof(PanelImageView.HintPoint)] = name == "stepper-plain" ? (0.31, 0.62) : null,
            [nameof(PanelImageView.Interactive)] = name != "stepper-plain",
            [nameof(PanelImageView.SelectedHoldId)] = name == "stepper-retarget" ? holds[2].Id : null,
            [nameof(PanelImageView.HoldColors)] = name == "stepper-retarget" ? new Dictionary<Guid, string> { [holds[1].Id] = "#33aa44" } : null,
            [nameof(PanelImageView.Ghost)] = name == "stepper-retarget" ? holds[3] : null,
            [nameof(PanelImageView.GhostMovedTo)] = name == "stepper-retarget" ? (0.75, 0.25) : null,
            [nameof(PanelImageView.Editable)] = name == "stepper-editable",
            [nameof(PanelImageView.DragToMove)] = name == "stepper-editable",
            [nameof(PanelImageView.AddMode)] = name == "stepper-editable",
        };

        var html = await OverlaySnapshotRenderer.RenderAsync<PanelImageView>(parameters);

        OverlaySnapshotRenderer.AssertMatches(name, html, wallId);
    }

    private static IEnumerable<Dictionary<string, object?>> ShapeLooks()
    {
        var outline = Ring(0.031, 7);
        var holes = new List<IReadOnlyList<ShapePoint>> { Ring(0.01, 5), Ring(0.001, 2) };
        EventCallback<MouseEventArgs> click = EventCallback.Factory.Create<MouseEventArgs>(new object(), () => { });
        EventCallback<PointerEventArgs> down = EventCallback.Factory.Create<PointerEventArgs>(new object(), () => { });
        yield return Look(("Radius", 0.02), ("Fill", "rgba(1,2,3,0.4)"), ("Stroke", "#fff"), ("StrokeWidth", "0.25"));
        yield return Look(("Radius", 0.02), ("Foot", true), ("Fill", "none"), ("Stroke", "#FF9800"), ("StrokeWidth", "0.5"));
        yield return Look(("Radius", 0.02), ("Foot", true), ("FootScale", HoldShape.FullFoot), ("Dash", "0.6 0.4"), ("Kept", true));
        yield return Look(("ShapePoints", outline), ("Fill", "transparent"), ("Stroke", "red"), ("Kept", true), ("Dash", string.Empty));
        yield return Look(("ShapePoints", outline), ("ShapeHoles", holes), ("Stroke", "blue"), ("StrokeWidth", "0.333"),
            ("PointerEvents", "visiblePainted"), ("Style", "cursor: move;"), ("OnClick", click), ("OnPointerDown", down));
        yield return Look(("ShapePoints", outline), ("HoldId", Hold), ("StackKeys", "a b"), ("Style", "cursor: inherit; pointer-events: none;"));
        yield return Look(("ShapePoints", Ring(0.02, 2)), ("Radius", 0.015), ("OnClick", click));
        yield return Look(("Radius", 0.0), ("Fill", null), ("Stroke", null));
    }

    private static IEnumerable<Dictionary<string, object?>> OutlineLooks()
    {
        var outline = Ring(0.03, 8);
        var holes = new List<IReadOnlyList<ShapePoint>> { Ring(0.01, 6) };
        yield return Look(("Radius", 0.02), ("Color", BoulderHoldColors.Outline(HoldType.Start, HoldUsage.HandAndFoot)));
        yield return Look(("Radius", 0.02), ("Foot", true), ("FootScale", HoldShape.FullFoot), ("Look", "base"), ("Smooth", true));
        yield return Look(("ShapePoints", outline), ("Smooth", true), ("Color", BoulderHoldColors.Outline(HoldType.Normal, HoldUsage.FootOnly)));
        yield return Look(("ShapePoints", outline), ("ShapeHoles", holes), ("Smooth", true), ("Look", "dark"));
        yield return Look(("ShapePoints", outline), ("ShapeHoles", holes), ("Smooth", false), ("Color", HoldRingColor.Style("red")));
    }

    private static List<PanelHold> PanelHolds() =>
    [
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), 0.3, 0.6, 0.02, "red", HoldCategory.Hand),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), 0.5, 0.5, 0.015, "blue", HoldCategory.Foot),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"), 0.7, 0.3, 0.03, null, HoldCategory.Hand, Ring(0.03, 6), ShapeHoles: [Ring(0.01, 4)]),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b4"), 0.1, 0.1, 0.02, "green", HoldCategory.Hand, IsVirtual: true),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b5"), 0.9, 0.9, 0.02, "white", HoldCategory.Hand, Kept: true),
    ];

    private static Dictionary<string, object?> Look(params (string Name, object? Value)[] values)
    {
        var parameters = new Dictionary<string, object?> { ["X"] = 0.4567, ["Y"] = 0.1234 };
        foreach (var (name, value) in values)
        {
            parameters[name] = value;
        }

        return parameters;
    }

    private static List<ShapePoint> Ring(double radius, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ShapePoint
        {
            Dx = radius * Math.Cos(i * 2 * Math.PI / count) * (1 + (0.1 * (i % 2))),
            Dy = radius * Math.Sin(i * 2 * Math.PI / count),
        })];
}
