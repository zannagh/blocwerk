// <copyright file="HoldProtrusion.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// How far a hold stands out of its facet, measured from the photo-real scene's splat centres inside
/// its footprint (<see cref="HoldProtrusionEstimator"/>); stored as JSON in <see cref="Hold.ProtrusionMm"/>.
/// All heights are millimetres along the facet normal ABOVE THE FACET PLANE; the apex position is
/// relative to the hold's plane centre, like <see cref="Wall3DHoldShape"/>. <see cref="OutlineKey"/> ties
/// it to the outline it was measured with (<see cref="HoldFootprint.KeyOf"/>), so a moved or re-traced
/// hold falls back to the size estimate until the next measurement.
/// </summary>
/// <param name="Source">How the numbers were obtained.</param>
/// <param name="Points">Splat centres inside the footprint (0 for an estimate).</param>
/// <param name="BaseMm">
/// The surface the hold is bolted to, from the points just around it: ~0 on the bare wall, the local
/// height of a volume when the hold sits on one (auto-detected, unreviewed).
/// </param>
/// <param name="HeightMm">80th percentile of the points inside the footprint: the hold's body.</param>
/// <param name="ApexA">Apex along the facet's u, mm from the plane centre.</param>
/// <param name="ApexB">Apex along the facet's v, mm from the plane centre.</param>
/// <param name="ApexMm">95th percentile height: the hold's top.</param>
/// <param name="OutlineKey">The <see cref="HoldFootprint.KeyOf"/> of the hold when it was measured.</param>
/// <param name="ShiftA">
/// For a hold on a volume: where it really sits, along u, relative to its (flat-mapped) plane centre
/// (<see cref="HoldVolumeRemap"/>; auto, unreviewed). 0 otherwise. The other numbers were measured there.
/// </param>
/// <param name="ShiftB">The same along v.</param>
public sealed record HoldProtrusion(
    HoldProtrusionSource Source,
    int Points,
    double BaseMm,
    double HeightMm,
    double ApexA,
    double ApexB,
    double ApexMm,
    string OutlineKey,
    double ShiftA = 0,
    double ShiftB = 0)
{
    /// <summary>Above this base height the hold is taken to sit on a volume, not on the facet.</summary>
    public const double VolumeBaseMm = 40;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>True when the hold stands on something (a volume) well proud of its facet.</summary>
    [JsonIgnore]
    public bool OnVolume => BaseMm >= VolumeBaseMm;

    /// <summary>
    /// A stand-in from the hold's footprint size for holds the scene does not cover: fitted on The
    /// Attic's measured holds (protrusion ≈ 0.16·√(w·h) + 14 mm, median error 8 mm).
    /// </summary>
    /// <param name="widthMm">Footprint width along u.</param>
    /// <param name="heightMm">Footprint height along v.</param>
    /// <param name="outlineKey">The hold's outline key.</param>
    /// <returns>The estimate.</returns>
    public static HoldProtrusion Estimate(double widthMm, double heightMm, string outlineKey)
    {
        var size = Math.Sqrt(Math.Max(widthMm, 1) * Math.Max(heightMm, 1));
        var body = Math.Clamp((0.16 * size) + 14, 8, 90);
        return new HoldProtrusion(HoldProtrusionSource.Estimate, 0, 0, body, 0, 0, body * 1.3, outlineKey);
    }

    /// <summary>The stored protrusion of <paramref name="hold"/> when it still matches its outline; else null.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>The protrusion or null.</returns>
    public static HoldProtrusion? For(Hold hold)
    {
        if (string.IsNullOrEmpty(hold.ProtrusionMm))
        {
            return null;
        }

        try
        {
            var p = JsonSerializer.Deserialize<HoldProtrusion>(hold.ProtrusionMm, Options);
            return p is not null && p.OutlineKey == HoldFootprint.KeyOf(hold) ? p : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The JSON stored in <see cref="Hold.ProtrusionMm"/>.</summary>
    /// <returns>The JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>How a <see cref="HoldProtrusion"/> was obtained. Serialised by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HoldProtrusionSource>))]
public enum HoldProtrusionSource
{
    /// <summary>Measured from the photo-real scene's splat centres inside the footprint.</summary>
    Splat,

    /// <summary>Too few scene points: estimated from the footprint size.</summary>
    Estimate,

    /// <summary>Measured from the capture's sparse points (no photo-real view): coarser, a few points per hold.</summary>
    Sparse,
}
