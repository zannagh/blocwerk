// <copyright file="WallPhotoEditor.ShapeTools.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The Shape tool's edits that run in the browser (wall-shape-drag.js): dragging a point, the eraser and the
/// brush. The browser draws the preview and sends the finished outline once, on release; here it is checked
/// (<see cref="ShapeEditValidation"/>) and written into the working copy with an undo snapshot, like every other
/// editor edit, so Discard takes it back and the save writes it through the normal batch.
/// </summary>
public partial class WallPhotoEditor
{
    private ShapeStrokeTool shapeStrokeTool = ShapeStrokeTool.Points;
    private double shapeStrokeRadiusPx = 14;
    private bool shapeBrushSubtract;

    /// <summary>Why the last browser edit was refused, shown in the option row until the next one.</summary>
    private string? shapeEditRefusal;

    /// <summary>
    /// The <c>data-shape-tool</c> of the photo: set while a hold's outline is being edited, which is what lets
    /// wall-shape-drag.js (and through it viewport-gestures.js) claim a press. Null otherwise.
    /// </summary>
    private string? ShapeToolAttribute => CanEdit && _mode == HoldTouchupTool.Shape && EditedShapeHold() is not null
        ? shapeStrokeTool switch
        {
            ShapeStrokeTool.Eraser => "eraser",
            ShapeStrokeTool.Brush => "brush",
            _ => "handles",
        }
        : null;

    /// <summary>
    /// The end of a Shape-tool edit that ran in the browser: the one call it makes.
    /// </summary>
    /// <param name="holdId">The edited hold, as rendered in <c>data-shape-hold</c>.</param>
    /// <param name="kind"><c>drag</c>, <c>erase</c> or <c>brush</c>. None of them may leave a crossing outline: a dragged point that
    /// would is refused with a message, and the browser shows the outline as it was.</param>
    /// <param name="flat">The new points as offsets from the hold's centre: dx0, dy0, dx1, dy1, ...</param>
    /// <param name="control">True when they are the control points of the hold's smooth outline.</param>
    /// <returns>False when the edit was refused; the browser then shows the hold as it was.</returns>
    [JSInvokable]
    public bool CommitShapeEdit(string holdId, string kind, double[] flat, bool control)
    {
        shapeEditRefusal = null;
        if (!CanEdit || _mode != HoldTouchupTool.Shape || !Guid.TryParse(holdId, out var id) || _shapeEditHoldId != id
            || flat is null || flat.Length % 2 != 0 || Holds.FirstOrDefault(h => h.Id == id) is not { } hold)
        {
            return false;
        }

        var points = Enumerable.Range(0, flat.Length / 2).Select(i => new ShapePoint { Dx = flat[2 * i], Dy = flat[(2 * i) + 1] }).ToList();
        bool smooth = control && HoldOutlineSpline.IsSmooth(hold);
        string? problem = control && !smooth
            ? "The outline is no longer smooth. Pick the hold again."
            : ShapeEditValidation.Problem(hold.X, hold.Y, points, smooth, requireSimple: true);
        if (problem is not null)
        {
            shapeEditRefusal = problem;
            StateHasChanged();
            return false;
        }

        ApplyShapeEdit(hold, points, smooth);
        StateHasChanged();
        return true;
    }

    private Hold? EditedShapeHold() =>
        _shapeEditHoldId is { } id && Holds.FirstOrDefault(h => h.Id == id) is { ShapePoints.Count: >= 3 } hold ? hold : null;

    private void ApplyShapeEdit(Hold hold, List<ShapePoint> points, bool smooth)
    {
        TakeSnapshot();
        if (smooth)
        {
            hold.ShapeControlPoints = points;
            HoldOutlineSpline.Refresh(hold);
        }
        else
        {
            hold.ShapePoints = points;
            hold.ShapeControlPoints = null;
        }

        DropPocketHoles(hold);
        TrackUpdate(hold);
    }

    private void SetShapeStrokeTool(ShapeStrokeTool tool)
    {
        shapeStrokeTool = tool;
        shapeEditRefusal = null;
    }

    /// <summary>
    /// <c>1</c> / <c>2</c> / <c>3</c> pick Points / Eraser / Brush, in the Shape tool only (the digits are the paint
    /// colours in the Paint tool, which <see cref="TryPaintShortcut"/> handles first).
    /// </summary>
    private bool TryShapeStrokeShortcut(string key)
    {
        if (!CanEdit || _mode != HoldTouchupTool.Shape)
        {
            return false;
        }

        ShapeStrokeTool? tool = key switch
        {
            "1" => ShapeStrokeTool.Points,
            "2" => ShapeStrokeTool.Eraser,
            "3" => ShapeStrokeTool.Brush,
            _ => null,
        };
        if (tool is { } picked)
        {
            SetShapeStrokeTool(picked);
        }

        return tool is not null;
    }

    private void SetShapeStrokeRadius(double px) => shapeStrokeRadiusPx = Math.Clamp(px, 4, 60);

    private void SetShapeBrushSubtract(bool subtract) => shapeBrushSubtract = subtract;
}
