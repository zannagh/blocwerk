// <copyright file="WallLookOverService.Discarded.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "Add as a hold" for a detection the update discarded (<see cref="DiscardedDetections"/>): the detection becomes a
/// live hold through the hold editor's own add (<see cref="IWallService.AddHoldAsync"/>: its panel and generation, the
/// 3D placement that follows, the journal), with the detected outline, no name, marked for review like every new
/// detection, and a colour suggested from its photo. Undo removes the hold again while no boulder uses it. The bulk
/// step adds only what is still likely a missed hold when it runs.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <inheritdoc/>
    public async Task<LookOverBulkResult> AddDetectionsAsync(Guid wallId, IReadOnlyCollection<Guid> findingIds, CancellationToken ct = default)
    {
        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            if (!access.CanEdit)
            {
                throw new UnauthorizedAccessException($"User {access.UserId} may not add holds on wall {wallId}.");
            }

            // Only what the user was shown and confirmed, and of that only what is still open and the detector was sure of.
            var asked = findingIds.Distinct().ToList();
            var open = (await db.WallLookOverFindings
                    .Where(f => f.WallId == wallId && f.Kind == LookOverFindingKind.DiscardedDetection && f.Status == LookOverFindingStatus.Open
                        && asked.Contains(f.Id) && f.PanelId != null && f.Confidence >= LookOverRules.SureDetection)
                    .ToListAsync(ct))
                .OrderByDescending(f => f.Confidence)
                .ThenBy(f => f.Id)
                .ToList();
            var areas = await DiscardedDetections.LiveAreasAsync(db, wallId, open.Select(f => f.PanelId!.Value).Distinct().ToList(), ct);
            using var batch = BeginBatch(wallId);
            using var feedScope = FeedScope.Begin(wallId, BatchLabel);
            var added = new List<Guid>();
            foreach (var finding in open)
            {
                // Against the holds as they are NOW, those just added included: of two detections of one hold only the surer is added.
                var (size, holds) = areas[finding.PanelId!.Value];
                var area = DiscardedDetections.Area(finding, size);
                if (DiscardedDetections.Overlap(area, holds) >= LookOverRules.NoOverlapBelow)
                {
                    continue;
                }

                added.Add(await AddOneAsync(db, finding, access.UserId, ct));
                holds.Add(area);
            }

            await SuggestColorAsync(db, wallId, added, ct);
            logger.LogInformation("{Count} of {Asked} discarded detections added as holds at once on wall {WallId} by {UserId}", added.Count, asked.Count, wallId, access.UserId);
            return new LookOverBulkResult(added.Count, asked.Count);
        }
    }

    /// <summary>Claims one open detection, adds it and settles its finding; a refusal leaves the finding open.</summary>
    private async Task<Guid> AddOneAsync(BlocwerkDbContext db, WallLookOverFinding finding, Guid userId, CancellationToken ct)
    {
        await ClaimForChangeAsync(db, finding, ct);
        try
        {
            var hold = await AddDetectionAsync(db, finding, ct);
            MarkSettled(finding, LookOverAnswer.AddHold, userId, 0);
            await db.SaveChangesAsync(ct);
            return hold.Id;
        }
        catch
        {
            await ReleaseAsync(db, finding);
            throw;
        }
    }

    /// <summary>
    /// Adds the detection of a claimed finding as a live hold on its photo and ties the finding to it (the caller
    /// saves). Refused when that photo is no longer the live one, or while a wall update is staged.
    /// </summary>
    private async Task<Hold> AddDetectionAsync(BlocwerkDbContext db, WallLookOverFinding finding, CancellationToken ct)
    {
        const string Replaced = "This photo is no longer the current one, or a wall update is in progress. The detection cannot be added now.";
        if (finding.PanelId is not { } panelId || !(await LiveWallHolds.LoadPanelIdsAsync(db, finding.WallId, ct)).Contains(panelId))
        {
            throw new UserFacingException(Replaced);
        }

        var outline = finding.ShapePoints?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
        var added = await walls.AddHoldAsync(finding.WallId, finding.X, finding.Y, finding.Radius, null, shapePoints: outline, wallPanelId: panelId);
        if (added.WallPanelId != panelId)
        {
            await walls.DeleteHoldAsync(added.Id);
            throw new UserFacingException(Replaced);
        }

        var hold = await db.Holds.FirstAsync(h => h.Id == added.Id, ct);
        (hold.IsAutoDetected, hold.Confidence, hold.NeedsReview) = (true, finding.Confidence ?? 0, true);
        hold.ShapeHoles = ShapePoint.CloneRings(finding.ShapeHoles);
        finding.HoldId = hold.Id;
        await CloseConflictingAsync(db, finding, ct);
        return hold;
    }

    /// <summary>"The 3D model sees a hold here, add it in the hold editor" is done once the detection on that spot is added.</summary>
    private static async Task CloseConflictingAsync(BlocwerkDbContext db, WallLookOverFinding added, CancellationToken ct)
    {
        var conflicting = await db.WallLookOverFindings
            .Where(f => f.WallId == added.WallId && f.PanelId == added.PanelId && f.Kind == LookOverFindingKind.ConflictingNew && f.Status == LookOverFindingStatus.Open)
            .ToListAsync(ct);
        foreach (var finding in conflicting)
        {
            var (dx, dy) = (finding.X - added.X, finding.Y - added.Y);
            if (Math.Sqrt((dx * dx) + (dy * dy)) <= Math.Max(finding.Radius, added.Radius))
            {
                (finding.Status, finding.ClosedReason, finding.ClosedAt) = (LookOverFindingStatus.Closed, LookOverCloseReason.HoldAddedHere, DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>
    /// An answer about a discarded detection can be taken back whenever: "not a hold" changed nothing, and an added
    /// hold is removed again while it is still a live hold no current boulder uses.
    /// </summary>
    private static bool CanUndoDiscarded(WallLookOverFinding f, Dictionary<Guid, LookOverHoldNow> holds, ILookup<Guid, LookOverBoulder> boulders) =>
        f.Status == LookOverFindingStatus.Settled
        && (f.Answer == LookOverAnswer.Settle
            || (f.Answer == LookOverAnswer.AddHold && f.HoldId is { } id && holds.GetValueOrDefault(id) is { Live: true } && !boulders[id].Any()));
}
