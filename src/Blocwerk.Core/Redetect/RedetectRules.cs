// <copyright file="RedetectRules.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Redetect;

/// <summary>The small rules of a re-detect about one detection: is it traced, is it turned down, is a circle its placeholder. Pure.</summary>
internal static class RedetectRules
{
    /// <summary>True when the detection has an outline that was really traced: not a plain circle and not a rough shape.</summary>
    public static bool IsTraced(Hold detection) =>
        detection.ShapePoints is { Count: >= 3 } && detection.OutlineConfidence is not <= HoldOutlineRefiner.CircleConfidenceCeiling;

    /// <summary>True when the detection lies on a spot where a person already said there is no hold.</summary>
    public static bool OnTurnedDown(RedetectInput input, int detection, HoldSamenessOptions options)
    {
        var shape = input.Detections[detection].Shape;
        return input.TurnedDown.Any(spot =>
            Math.Abs(spot.X - shape.X) <= spot.Radius + shape.Radius
            && Math.Abs(spot.Y - shape.Y) <= (spot.Radius + shape.Radius) * 2
            && HoldSameness.SamePlace(HoldSameness.Measure(spot, spot.ShapePoints is { Count: >= 3 } ? shape : new Hold { X = shape.X, Y = shape.Y, Radius = shape.Radius }), options));
    }

    /// <summary>True for a hold a re-detect may look at: not virtual and not hidden as kept but not confirmed.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether it takes part.</returns>
    public static bool IsMatchable(Hold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return !hold.IsVirtual && hold.KeptUnconfirmedGeneration is null;
    }

    /// <summary>True when a person placed the hold or drew or edited its shape: that shape is never replaced.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether its shape is hand-made.</returns>
    public static bool IsHandShaped(Hold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return HoldMergeRules.IsHandMade(hold) || hold.ShapeControlPoints is { Count: > 0 };
    }

    /// <summary>
    /// True when a stored plain circle stands for the traced detection around it: the circle only marks the spot, so a
    /// detection that is clearly larger or smaller than it is still that hold while the circle's centre lies in it.
    /// </summary>
    public static bool IsPlaceholderOf(Hold circle, Hold detection, HoldOverlap overlap, HoldSamenessOptions options) =>
        detection.ShapePoints is { Count: >= 3 }
        && overlap.SizeRatio <= options.MaxPlaceholderSizeRatio
        && ShapeGeometry.Contains(HoldShapePolygon.Of(detection), new P2(circle.X, circle.Y));
}
