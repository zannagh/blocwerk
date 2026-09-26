// <copyright file="WallVolumeService.Replace.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>Storing a detection run's volumes: what an admin decided about the previous ones carries over.</summary>
public sealed partial class WallVolumeService
{
    /// <summary>A re-detected volume keeps the hidden flag and flat-sides choice of an old one whose footprint centre is this near, mm.</summary>
    private const double SameVolumeMm = 120;

    /// <summary>A candidate overlapping a removed volume's footprint at least this much (IoU) is the same false detection.</summary>
    private const double RemovedOverlap = 0.5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Replaces the model's volumes by the accepted candidates. Removed volumes stay (for the undo) and every candidate
    /// overlapping one (IoU ≥ 0.5 on the same facet) is skipped; a volume found again keeps its hidden flag and flat-sides
    /// choice; a new one gets flat sides when the wall says so and the faces fit well. Returns the volumes stored.
    /// </summary>
    private static async Task<int> ReplaceVolumesAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, List<DetectedVolume> accepted, CancellationToken ct)
    {
        var old = await db.WallVolumes.Where(v => v.GeometryModelId == modelId).ToListAsync(ct);
        var removed = old.Where(v => v.IsRemoved).ToList();
        var removedRings = removed.Select(v => (v.FacetId, Ring: Footprint(v.FootprintJson))).ToList();
        var previous = old.Where(v => !v.IsRemoved)
            .Select(v => (v.FacetId, Centre: Centre(Footprint(v.FootprintJson)), v.IsHidden, v.HasFlatSides)).ToList();
        db.WallVolumes.RemoveRange(old.Where(v => !v.IsRemoved));
        var wallFlat = await db.Walls.Where(w => w.Id == wallId).Select(w => w.VolumesHaveFlatSides).FirstOrDefaultAsync(ct);
        var index = 0;
        foreach (var v in accepted.OrderBy(v => v.FacetId, StringComparer.Ordinal).ThenBy(v => v.Footprint.Average(p => p.A)))
        {
            if (removedRings.Any(r => r.FacetId == v.FacetId && VolumeRings.IoU(r.Ring, v.Footprint) >= RemovedOverlap))
            {
                continue;
            }

            var centre = Centre(v.Footprint);
            var match = previous.Where(p => p.FacetId == v.FacetId && Distance(p.Centre, centre) < SameVolumeMm)
                .Select(p => ((bool IsHidden, bool HasFlatSides)?)(p.IsHidden, p.HasFlatSides)).FirstOrDefault();
            var row = NewRow(wallId, modelId, v, ++index);
            row.IsHidden = match?.IsHidden ?? false;
            if (match?.HasFlatSides ?? wallFlat)
            {
                // A volume the admin gave flat sides keeps them; the wall's default only applies where they fit well.
                WallVolumeShapes.SetFlatSides(row, on: true, force: match is not null);
            }

            db.WallVolumes.Add(row);
        }

        var stored = index;
        foreach (var r in removed.OrderBy(r => r.Index))
        {
            r.Index = ++index;
        }

        await db.SaveChangesAsync(ct);
        return stored;
    }

    private static WallVolume NewRow(Guid wallId, Guid modelId, DetectedVolume v, int index) => new()
    {
        WallId = wallId,
        GeometryModelId = modelId,
        FacetId = v.FacetId,
        Index = index,
        FootprintJson = JsonSerializer.Serialize(v.Footprint.Select(p => new[] { Math.Round(p.A, 1), Math.Round(p.B, 1) }), Json),
        SurfaceJson = v.Surface!.ToJson(),
        AreaM2 = v.AreaM2,
        HeightMm = v.HeightMm,
        Confidence = v.Confidence,
    };

    /// <summary>A stored footprint; empty when malformed.</summary>
    private static List<(double A, double B)> Footprint(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<double[][]>(json, Json) ?? [])
                .Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static (double A, double B) Centre(IReadOnlyList<(double A, double B)> ring) =>
        ring.Count == 0 ? (0, 0) : (ring.Average(p => p.A), ring.Average(p => p.B));

    private static double Distance((double A, double B) p, (double A, double B) q) =>
        Math.Sqrt(((p.A - q.A) * (p.A - q.A)) + ((p.B - q.B) * (p.B - q.B)));
}
