// <copyright file="WallBigUpdateService.NewTriageInputs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// What the new-hold triage reads besides the holds: the printed markers on the staged photo and, for a
/// neighbour panel, the staged centre it overlaps.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// The marker pass the triage runs itself: every dictionary id, small and oblique ones too, on the photo
    /// and on a brightened copy (markers on a dark kickboard or volume only decode there). A quad still has
    /// to be a dark square on white paper (the plan-print quiet-zone floor), so a dark hold is not taken for one.
    /// </summary>
    private static readonly MarkerDetectionOptions RescueMarkerOptions = new()
    {
        AllowedIds = Enumerable.Range(0, 50).ToHashSet(),
        MinSidePx = 8,
        MaxEdgeRatio = 10,
        RefineCorners = false,
        MinQuietZoneContrast = MarkerDetectionOptions.PlanMinQuietZoneContrast,
        BrightenedPass = true,
    };

    /// <summary>For a neighbour panel with overlap proposals against the triaged centre, its overlap owner.</summary>
    private static Func<(int Width, int Height), OverlapOwner?> OwnerSource(
        TriagedCentre? centre, NeighbourOverlap? overlap, IReadOnlyList<Hold> staged) =>
        size =>
        {
            if (centre is null || overlap is null)
            {
                return null;
            }

            var own = staged.ToDictionary(h => h.Id);
            var pairs = new List<PointPair>();
            foreach (var p in overlap.Proposals)
            {
                if (centre.Holds.TryGetValue(p.HoldAId, out var c) && own.TryGetValue(p.HoldBId, out var n))
                {
                    pairs.Add(new PointPair(
                        n.X * size.Width, n.Y * size.Height, c.X * centre.Size.Width, c.Y * centre.Size.Height));
                }
            }

            return new OverlapOwner(pairs, centre.Size, centre.KeptNew, size);
        };

    private async Task<List<IReadOnlyList<(double X, double Y)>>> RescuedMarkerQuadsAsync(byte[] photo)
    {
        if (markerDetection is null)
        {
            return [];
        }

        try
        {
            var found = await markerDetection.DetectAsync(photo, RescueMarkerOptions, CancellationToken.None);
            return found.Markers.Select(m => m.CornersPx)
                .Concat(found.Rejected.Where(r => r.Reason == MarkerRejectionReason.DuplicateId).Select(r => r.CornersPx))
                .Select(q => (IReadOnlyList<(double X, double Y)>)q.Select(c => (c.X, c.Y)).ToList())
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The triage's marker pass failed; only the staged marker observations are used");
            return [];
        }
    }

    private static async Task<List<IReadOnlyList<(double X, double Y)>>> StagedMarkerQuadsAsync(
        BlocwerkDbContext db, Guid panelId, (int Width, int Height) size)
    {
        var rows = await db.WallMarkerObservations
            .Where(o => o.WallPanelId == panelId && o.FromStagedPhoto && !o.Synthetic)
            .Select(o => o.CornersJson)
            .ToListAsync();
        var quads = new List<IReadOnlyList<(double X, double Y)>>();
        foreach (var json in rows)
        {
            try
            {
                if (JsonSerializer.Deserialize<double[][]>(json) is { Length: 4 } corners && corners.All(c => c.Length >= 2))
                {
                    quads.Add(corners.Select(c => (c[0] * size.Width, c[1] * size.Height)).ToList());
                }
            }
            catch (JsonException)
            {
                // An unreadable row is simply not a marker to avoid.
            }
        }

        return quads;
    }
}
