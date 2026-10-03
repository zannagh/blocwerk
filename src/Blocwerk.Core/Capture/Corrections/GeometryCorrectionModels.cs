// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>The kinds of correction; each creates a new model version.</summary>
public enum GeometryCorrectionKind
{
    /// <summary>"Make sizes exact": a measured distance on a capture photo sets the scale.</summary>
    Scale = 0,

    /// <summary>"This surface is vertical": gravity re-derived so that surface is plumb.</summary>
    Vertical = 1,

    /// <summary>"Not part of the wall": a surface accepted by mistake is dropped.</summary>
    Drop = 2,
}

/// <summary>What the result card and the correction controls show about the wall's active model.</summary>
/// <param name="ModelId">The active model.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="FromFeatures">Solved from photo features (no markers).</param>
/// <param name="Scale">Where its sizes come from, in words.</param>
/// <param name="ScaleIsEstimate">True when the sizes are only estimated ("Make exact" is offered).</param>
/// <param name="Gravity">Where its angles come from, in words.</param>
/// <param name="GravityKnown">False when "up" was not measured ("Which surface is vertical?" is offered).</param>
/// <param name="CaptureId">The finished capture whose photos the model was solved from, when its photos are still stored.</param>
/// <param name="Photos">Those photos that have a solved camera (the ones a distance can be measured on).</param>
/// <param name="Facets">The model's surfaces.</param>
/// <param name="LastCorrection">What the last correction did, when the model is one.</param>
public sealed record GeometryCorrectionState(
    Guid ModelId,
    DateTimeOffset CreatedAt,
    bool FromFeatures,
    string Scale,
    bool ScaleIsEstimate,
    string Gravity,
    bool GravityKnown,
    Guid? CaptureId,
    IReadOnlyList<CapturePhotoResult> Photos,
    IReadOnlyList<GeometryCorrectionFacet> Facets,
    string? LastCorrection);

/// <summary>A surface of the model, for the correction controls.</summary>
/// <param name="Id">The facet id.</param>
/// <param name="Name">Its segment's name.</param>
/// <param name="AngleDeg">Its measured angle (null when "up" is unknown).</param>
/// <param name="IsReference">The surface that defines the frame (it cannot be dropped).</param>
/// <param name="IsVertical">The surface declared vertical, when one was.</param>
public sealed record GeometryCorrectionFacet(string Id, string Name, double? AngleDeg, bool IsReference, bool IsVertical);

/// <summary>What a correction did.</summary>
/// <param name="Kind">The correction.</param>
/// <param name="ModelId">The new, active model version.</param>
/// <param name="PreviousModelId">The model it was derived from (kept in the history, revertable).</param>
/// <param name="Scale">The scale it applied (1 = none).</param>
/// <param name="RotationDeg">The rotation it applied, degrees.</param>
/// <param name="Summary">In words, e.g. "Sizes made exact: ×1.012 (the model had 1186 mm where you measured 1200 mm)".</param>
public sealed record GeometryCorrectionResult(
    GeometryCorrectionKind Kind, Guid ModelId, Guid PreviousModelId, double Scale, double RotationDeg, string Summary);
