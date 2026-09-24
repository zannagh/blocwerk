// <copyright file="HoldFootprint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// A hold's contact footprint on its facet, stored as JSON in <see cref="Hold.FootprintMm"/>. The outline
/// is in facet-plane millimetres RELATIVE to the hold's stored plane centre, like <see cref="Wall3DHoldShape"/>.
/// <see cref="OutlineKey"/> ties it to the traced outline it was refined from, so a re-traced hold falls
/// back to the plain projection until the next refinement.
/// </summary>
/// <param name="Source">How the footprint was obtained.</param>
/// <param name="Views">How many photos contributed (1 for the single-view correction).</param>
/// <param name="SpreadDeg">Largest angle between two contributing view rays (0 for one view).</param>
/// <param name="HeightMm">The protrusion used by the single-view correction; null for multi-view.</param>
/// <param name="OutlineKey">The <see cref="KeyOf"/> of the outline it was refined from.</param>
/// <param name="Outline">The footprint ring, <c>[da, db]</c> per vertex.</param>
/// <param name="ShiftA">
/// How far (along u, mm) the capture photos moved the hold from where its panel photo mapped it
/// (<see cref="HoldPositionRefiner"/>); already applied to <paramref name="Outline"/>, kept to show and undo
/// it. 0 when they agreed with the panel photo (or never were asked).
/// </param>
/// <param name="ShiftB">The same along v.</param>
public sealed record HoldFootprint(
    HoldFootprintSource Source,
    int Views,
    double SpreadDeg,
    double? HeightMm,
    string OutlineKey,
    IReadOnlyList<double[]> Outline,
    double ShiftA = 0,
    double ShiftB = 0)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>A short, stable fingerprint of a hold's traced outline and position.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(Hold hold)
    {
        return ((uint)StableHash(hold)).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The stored footprint of <paramref name="hold"/> when it still matches its outline; else null.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>The footprint or null.</returns>
    public static HoldFootprint? For(Hold hold)
    {
        if (string.IsNullOrEmpty(hold.FootprintMm))
        {
            return null;
        }

        try
        {
            var fp = JsonSerializer.Deserialize<HoldFootprint>(hold.FootprintMm, Options);
            return fp is { Outline.Count: >= 3 } && fp.OutlineKey == KeyOf(hold) ? fp : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The JSON stored in <see cref="Hold.FootprintMm"/>.</summary>
    /// <returns>The JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>FNV-1a over the rounded outline, so the key survives process restarts (HashCode does not).</summary>
    private static int StableHash(Hold hold)
    {
        unchecked
        {
            var h = (int)2166136261;
            void Mix(double v)
            {
                var bits = BitConverter.DoubleToInt64Bits(Math.Round(v, 5));
                h = (h ^ (int)bits) * 16777619;
                h = (h ^ (int)(bits >> 32)) * 16777619;
            }

            Mix(hold.X);
            Mix(hold.Y);
            foreach (var p in hold.ShapePoints ?? [])
            {
                Mix(p.Dx);
                Mix(p.Dy);
            }

            return h;
        }
    }
}

/// <summary>How a <see cref="HoldFootprint"/> was obtained. Serialised by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HoldFootprintSource>))]
public enum HoldFootprintSource
{
    /// <summary>Silhouettes from ≥ 2 well-separated views, projected onto the facet and intersected.</summary>
    MultiView,

    /// <summary>One view: the silhouette shortened along its view direction by an estimated protrusion (approximate).</summary>
    SingleViewCorrected,
}
