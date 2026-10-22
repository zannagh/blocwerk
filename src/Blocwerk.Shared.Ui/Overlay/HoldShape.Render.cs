// <copyright file="HoldShape.Render.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Shared.Ui.Overlay;

/// <summary>
/// A hold re-renders only when something it draws changed. Its list parameters (and, on most surfaces, fresh
/// handler lambdas) made Blazor treat every parent render as a change, so the wall editor re-rendered and
/// re-diffed every polygon on the panel for any state change at all. The outline is compared by VALUE: editors
/// edit the stored points in place, so the list reference alone would miss a reshape.
/// </summary>
public partial class HoldShape
{
    private HoldShapeLook lastLook;
    private double[]? lastGeometry;
    private bool changed = true;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        var look = new HoldShapeLook(
            HoldId, StackKeys, X, Y, Radius, Foot, FootScale, Fill, Stroke, StrokeWidth, Dash, Kept, PointerEvents, Style,
            OnClick, OnPointerDown, OnMouseOver, OnMouseOut);
        changed = lastGeometry is null || !look.Equals(lastLook) || !HoldShapeLook.SameGeometry(lastGeometry, ShapePoints, ShapeHoles);
        if (changed)
        {
            lastLook = look;
            lastGeometry = HoldShapeLook.Geometry(ShapePoints, ShapeHoles);
        }
    }

    /// <inheritdoc />
    protected override bool ShouldRender() => changed;
}
