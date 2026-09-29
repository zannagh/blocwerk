// <copyright file="WallGeometryRejectedObservation.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry;

/// <summary>
/// One detection left out of the model (<c>quality.rejectedObservations</c>). The solver removes one whose
/// reprojection residual was far above the capture's typical one and, for a marker seen in other photos
/// too, those photos place the marker somewhere this detection does not fit; or, when the whole model came out
/// implausible, the worst offenders before it solved again. The app adds the detections it left out before the
/// solve: no white border around them, or not fitting the plan layout.
/// </summary>
public sealed record WallGeometryRejectedObservation
{
    /// <summary>Solver reason: the other photos place the marker elsewhere.</summary>
    public const string InconsistentWithOtherViews = "inconsistent-with-other-views";

    /// <summary>Solver reason: seen in one photo only, and not a flat square there.</summary>
    public const string SingleViewMisfit = "single-view-misfit";

    /// <summary>App reason: the detection does not fit the wall's marker plan layout in its photo.</summary>
    public const string PlanLayout = "plan-layout";

    /// <summary>App reason: no white border around the detection (a hold or a shadow that decoded as an id).</summary>
    public const string NoQuietZone = "no-quiet-zone";

    /// <summary>Solver reason: dropped as a worst offender after the model came out implausible, then re-solved.</summary>
    public const string ImplausibleModel = "implausible-model";

    /// <summary>The photo's name in the solve request.</summary>
    public string Photo { get; init; } = string.Empty;

    /// <summary>The marker id detected there.</summary>
    public int Id { get; init; }

    /// <summary>Photos the marker was detected in before the rejection.</summary>
    public int? Views { get; init; }

    /// <summary>The detection's reprojection RMS in the free solve, px.</summary>
    public double? ResidualPx { get; init; }

    /// <summary>The rejection threshold of this capture, px.</summary>
    public double? ThresholdPx { get; init; }

    /// <summary>The detection against the marker as its OTHER photos place it, px; null for a single-view marker.</summary>
    public double? OtherViewsResidualPx { get; init; }

    /// <summary>
    /// <see cref="InconsistentWithOtherViews"/>, <see cref="SingleViewMisfit"/>, <see cref="ImplausibleModel"/>,
    /// <see cref="PlanLayout"/> or <see cref="NoQuietZone"/>.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>The reason in more words, when the stage that left it out gave one.</summary>
    public string? Detail { get; init; }

    /// <summary>True when this was the marker's only detection, so the marker is not in the model.</summary>
    public bool MarkerDropped { get; init; }
}
