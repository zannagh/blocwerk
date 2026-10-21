// <copyright file="WallBigUpdateService.NewTriageInputs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
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
    /// <summary>The lenient marker pass: every id, small and steep quads, duplicates kept. Re-detect uses it too.</summary>
    internal static readonly MarkerDetectionOptions RescueMarkerOptions = new()
    {
        AllowedIds = Enumerable.Range(0, 50).ToHashSet(),
        MinSidePx = 8,
        MaxEdgeRatio = 10,
        RefineCorners = false,
        MinQuietZoneContrast = MarkerDetectionOptions.PlanMinQuietZoneContrast,
        BrightenedPass = true,
    };

    /// <summary>
    /// For a neighbour panel with overlap proposals against the triaged centre, its overlap owner, with which of its pairs
    /// are backed by measured anchors (<see cref="NeighbourPanelRule.IsMeasured"/>).
    /// </summary>
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
            var measured = new List<PointPair>();
            foreach (var p in overlap.Proposals)
            {
                if (centre.Holds.TryGetValue(p.HoldAId, out var c) && own.TryGetValue(p.HoldBId, out var n))
                {
                    var pair = new PointPair(n.X * size.Width, n.Y * size.Height, c.X * centre.Size.Width, c.Y * centre.Size.Height);
                    pairs.Add(pair);
                    if (NeighbourPanelRule.IsMeasured(p.Confidence))
                    {
                        measured.Add(pair);
                    }
                }
            }

            return new OverlapOwner(pairs, centre.Size, centre.KeptNew, size, centre.Settled, measured, centre.Discarded);
        };

    /// <summary>
    /// Writes each suggested discard's reason onto its staged hold (and clears it where the triage no longer suggests
    /// one), so the reason survives the promote in the journal entry of the deleted detection. Tracked writes on a
    /// context of their own, inside the wall's open wall-update batch: the reason is part of what that batch replays
    /// (it used to be a set-based update the journal never saw, so a replayed wall had no reasons).
    /// </summary>
    private async Task PersistDiscardReasonsAsync(Guid wallId, int stagedGen, BigUpdateSession session)
    {
        var suggested = (session.SuggestedNewDiscards ?? new Dictionary<Guid, NewHoldDiscardReason>())
            .ToDictionary(s => s.Key, s => s.Value.ToString());
        var ids = suggested.Keys.ToList();

        await using var db = await dbContextFactory.CreateDbContextAsync();
        var holds = await db.Holds
            .Where(h => h.WallId == wallId && h.Generation == stagedGen && (ids.Contains(h.Id) || h.DiscardReason != null))
            .ToListAsync();

        // A pass without the 3D evidence cannot tell what the 3D model saw: the reasons an earlier pass with it wrote stay.
        var without3D = session.Evidence3DModelId is null;
        foreach (var hold in holds)
        {
            var reason = suggested.GetValueOrDefault(hold.Id);
            if (reason is null && without3D && hold.DiscardReason is nameof(NewHoldDiscardReason.KnownHoldIn3D) or nameof(NewHoldDiscardReason.OffWallIn3D))
            {
                continue;
            }

            if (hold.DiscardReason != reason)
            {
                hold.DiscardReason = reason;
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            using var batch = changeJournal?.BeginWallUpdateBatch(wallId);
            using var feedScope = FeedScope.Begin(wallId, ChangeJournal.WallUpdateBatchLabel);
            await db.SaveChangesAsync();
        }
    }

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

    /// <summary>
    /// The printed markers observed on one photo of a panel, RAW pixels: its staged photo, or (<paramref name="staged"/> false)
    /// its live one, which for the panel an update replaces is the old photo.
    /// </summary>
    private static async Task<List<IReadOnlyList<(double X, double Y)>>> MarkerQuadsAsync(
        BlocwerkDbContext db, Guid panelId, bool staged, (int Width, int Height) size)
    {
        var rows = await db.WallMarkerObservations
            .Where(o => o.WallPanelId == panelId && o.FromStagedPhoto == staged && !o.Synthetic)
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
