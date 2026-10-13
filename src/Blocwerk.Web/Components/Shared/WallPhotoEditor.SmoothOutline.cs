// <copyright file="WallPhotoEditor.SmoothOutline.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The Shape tool's "Smooth outline": the handles then move the control points of a curve instead of the
/// corners of a polygon, and the drawn outline is redrawn from them after every edit
/// (<see cref="HoldOutlineSpline"/>). Everything else in the editor keeps drawing <see cref="Hold.ShapePoints"/>.
/// </summary>
public partial class WallPhotoEditor
{
    /// <summary>The points the Shape tool's handles sit on: the control points of a smooth outline, else its corners.</summary>
    private static List<ShapePoint>? ShapeEditPoints(Hold hold) => HoldOutlineSpline.CurrentControlPoints(hold) ?? hold.ShapePoints;

    /// <summary>Redraws a smooth outline after its control points (<paramref name="edited"/>) were changed.</summary>
    private static void AfterShapeEdit(Hold hold, List<ShapePoint> edited)
    {
        if (ReferenceEquals(edited, hold.ShapeControlPoints))
        {
            HoldOutlineSpline.Refresh(hold);
        }
    }

    private static bool CanAddShapePoint(Hold hold) =>
        !HoldOutlineSpline.IsSmooth(hold) || hold.ShapeControlPoints!.Count < HoldOutlineSpline.MaxEditableControlPoints;

    private void ToggleSmoothOutline(Hold hold)
    {
        TakeSnapshot();
        if (!HoldOutlineSpline.SetSmooth(hold, !HoldOutlineSpline.IsSmooth(hold)))
        {
            return;
        }

        DropPocketHoles(hold);
        TrackUpdate(hold);
    }
}
