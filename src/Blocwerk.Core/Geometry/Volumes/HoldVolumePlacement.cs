// <copyright file="HoldVolumePlacement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// Where a hold really sits when it is on a volume (stored as <c>Hold.VolumePlacementJson</c>, additive): the
/// point on the volume's surface in its facet's frame and the surface normal there. The hold's panel position
/// and its flat facet position (<c>PlaneAMm</c>/<c>PlaneBMm</c>) are never changed; <see cref="FromA"/>/<see cref="FromB"/>
/// record the flat position it was derived from, so a moved hold's placement is recognisably stale.
/// </summary>
/// <param name="VolumeId">The volume.</param>
/// <param name="A">Surface point along u, mm.</param>
/// <param name="B">Surface point along v, mm.</param>
/// <param name="H">Surface point's height above the facet plane, mm.</param>
/// <param name="Normal">Surface normal in facet coordinates (a, b, height).</param>
/// <param name="FromA">The flat plane position along u it was derived from, mm.</param>
/// <param name="FromB">The flat plane position along v it was derived from, mm.</param>
/// <param name="Camera">Whose ray placed it: "panel" (the photo the hold was drawn on) or "frontal" (a capture photo).</param>
public sealed record HoldVolumePlacement(Guid VolumeId, double A, double B, double H, double[] Normal, double FromA, double FromB, string Camera)
{
    /// <summary>A placement is stale when the flat position moved more than this, mm.</summary>
    public const double StaleMm = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The storage form.</summary>
    /// <returns>JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses the storage form; null when malformed.</summary>
    /// <param name="json">JSON or null.</param>
    /// <returns>The placement or null.</returns>
    public static HoldVolumePlacement? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var p = JsonSerializer.Deserialize<HoldVolumePlacement>(json, Json);
            return p is { Normal.Length: 3 } && double.IsFinite(p.A) && double.IsFinite(p.B) && double.IsFinite(p.H) ? p : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether it still belongs to the hold's current flat position.</summary>
    /// <param name="planeA">The hold's plane a.</param>
    /// <param name="planeB">The hold's plane b.</param>
    /// <returns>True when current.</returns>
    public bool Matches(double? planeA, double? planeB) =>
        planeA is { } a && planeB is { } b && Math.Abs(a - FromA) <= StaleMm && Math.Abs(b - FromB) <= StaleMm;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"volume {VolumeId} ({A:0}, {B:0}, {H:0}) from ({FromA:0}, {FromB:0}) by {Camera}");
}
