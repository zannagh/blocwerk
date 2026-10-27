// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// The export's computed results (<see cref="CapturePackageResults"/>). Carried only when they belong to the capture's model
/// here: the model is the wall's active one (so the holds' 3D fields are measured on it), it has its finished photo-real view,
/// and the capture's follow-up chain is settled (nothing running, queued or to be derived again); a hold is carried only when its
/// 3D fields were computed from its current panel geometry (<see cref="PlacedGeometryAsync"/>). Otherwise the package
/// leaves them out with a warning and the target computes them, as before.
/// </summary>
public sealed partial class CapturePackageService
{
    private static async Task<CapturePackageResults?> LoadResultsAsync(
        BlocwerkDbContext db, WallCapture capture, WallGeometryModel model, List<string> warnings, CancellationToken ct)
    {
        var record = CaptureFollowUpRecord.Parse(capture.FollowUpJson);
        var settled = record is { Running: null, AfterCompletion: null, Rederive: false, RunAgain: false } && record.Steps.Count > 0;
        var splat = await db.WallGeometrySplats.AsNoTracking().FirstOrDefaultAsync(s => s.GeometryModelId == model.Id, ct);
        if (!model.IsActive || splat is null || !settled)
        {
            warnings.Add("The computed 3D results are not carried (the model is not active here, has no finished photo-real view, or its follow-up steps are not done): the target computes them.");
            return null;
        }

        var volumes = await db.WallVolumes.AsNoTracking().Where(v => v.GeometryModelId == model.Id).ToListAsync(ct);
        var proposals = await db.HoldProposals.AsNoTracking()
            .Where(p => p.WallId == capture.WallId && p.GeometryModelId == model.Id && p.Status == HoldProposalStatus.Pending)
            .ToListAsync(ct);
        var holds = await (await LiveWallHolds.QueryAsync(db, capture.WallId, ct)).AsNoTracking().ToListAsync(ct);
        var placed = await PlacedGeometryAsync(db, capture.WallId, model.Id, ct);
        var current = holds.Where(h => placed.GetValueOrDefault(h.Id) == HoldPlacementEntry.HashGeometry(h)).Select(CapturePackageHoldResult.Of).ToList();
        return new CapturePackageResults(splat, volumes, proposals, current, capture.FollowUpJson!);
    }

    /// <summary>
    /// Per hold, the panel geometry its 3D fields were computed from: the geometry hash of its newest unreverted placement on
    /// the model. A hold edited since (its refinement still owed) or never placed this way is not carried; the target refines it.
    /// </summary>
    private static async Task<Dictionary<Guid, string?>> PlacedGeometryAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct)
    {
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        var runs = await db.HoldPlacementRuns.AsNoTracking()
            .Where(r => r.WallId == wallId && r.GeometryModelId == modelId && r.RevertedAt == null)
            .Select(r => new { r.CreatedAt, r.HoldsJson })
            .ToListAsync(ct);
        return runs.OrderBy(r => r.CreatedAt)
            .SelectMany(r => HoldPlacementEntry.FromJson(r.HoldsJson))
            .GroupBy(e => e.HoldId)
            .ToDictionary(g => g.Key, g => g.Last().GeometryHash);
    }
}
