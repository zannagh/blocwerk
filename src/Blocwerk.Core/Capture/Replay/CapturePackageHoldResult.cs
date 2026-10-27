// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// One hold's 3D fields as the source computed them, and <see cref="Geometry"/>: the
/// <see cref="HoldPlacementEntry.HashGeometry"/> of the panel position and outline they were computed from. The target
/// takes them only for the same hold id with the same geometry hash.
/// </summary>
public sealed record CapturePackageHoldResult
{
    public Guid HoldId { get; init; }

    public required string Geometry { get; init; }

    public double? WidthMm { get; init; }

    public double? HeightMm { get; init; }

    public double? AreaMm2 { get; init; }

    public string? FacetId { get; init; }

    public double? PlaneAMm { get; init; }

    public double? PlaneBMm { get; init; }

    public string? FingerprintJson { get; init; }

    public string? MetricSource { get; init; }

    public string? FootprintMm { get; init; }

    public string? ProtrusionMm { get; init; }

    public string? VolumePlacementJson { get; init; }

    /// <summary>The hold's 3D fields (never its panel position, outline or anything else).</summary>
    public static CapturePackageHoldResult Of(Hold hold) => new()
    {
        HoldId = hold.Id,
        Geometry = HoldPlacementEntry.HashGeometry(hold),
        WidthMm = hold.WidthMm,
        HeightMm = hold.HeightMm,
        AreaMm2 = hold.AreaMm2,
        FacetId = hold.FacetId,
        PlaneAMm = hold.PlaneAMm,
        PlaneBMm = hold.PlaneBMm,
        FingerprintJson = hold.FingerprintJson,
        MetricSource = hold.MetricSource,
        FootprintMm = hold.FootprintMm,
        ProtrusionMm = hold.ProtrusionMm,
        VolumePlacementJson = hold.VolumePlacementJson,
    };

    /// <summary>Writes the 3D fields onto <paramref name="hold"/>; nothing else of it changes.</summary>
    public void ApplyTo(Hold hold)
    {
        (hold.WidthMm, hold.HeightMm, hold.AreaMm2) = (WidthMm, HeightMm, AreaMm2);
        (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm) = (FacetId, PlaneAMm, PlaneBMm);
        (hold.FingerprintJson, hold.MetricSource) = (FingerprintJson, MetricSource);
        (hold.FootprintMm, hold.ProtrusionMm, hold.VolumePlacementJson) = (FootprintMm, ProtrusionMm, VolumePlacementJson);
    }
}
