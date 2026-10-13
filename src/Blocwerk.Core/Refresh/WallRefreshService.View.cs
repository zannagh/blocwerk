// <copyright file="WallRefreshService.View.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>What the page shows of a run: its photos, the panel picks, the timeline and the 3D capture's own status.</summary>
public sealed partial class WallRefreshService
{
    private async Task<WallRefreshView> ToViewAsync(BlocwerkDbContext db, WallRefresh refresh)
    {
        var photos = await db.WallCapturePhotos.AsNoTracking()
            .Where(p => p.CaptureId == refresh.CaptureId)
            .OrderBy(p => p.Index)
            .Select(p => new { p.Id, p.Index, p.OriginalFileName, p.Width, p.Height, p.Focal35mm })
            .ToListAsync();
        var capture = refresh.CaptureStarted && refresh.CaptureId is { } id ? await captures.GetCaptureAsync(id) : null;
        var available = refresh.Status is WallRefreshStatus.Uploading or WallRefreshStatus.ReadyToStart
            && await CaptureAvailableAsync(db, refresh.WallId);
        return new WallRefreshView(
            refresh.Id,
            refresh.WallId,
            refresh.Status,
            photos.Select(p => new CapturePhotoResult(p.Id, p.Index, p.OriginalFileName, p.Width, p.Height, p.Focal35mm, [], [])).ToList(),
            RefreshTimeline.Videos(refresh),
            RefreshTimeline.Picks(refresh),
            RefreshTimeline.Steps(refresh),
            RefreshTimeline.Summary(refresh),
            capture,
            available,
            refresh.Error,
            refresh.CreatedAt,
            refresh.CaptureId,
            refresh.UpdateSessionId,
            HasActiveModel: await db.WallGeometryModels.AnyAsync(m => m.WallId == refresh.WallId && m.IsActive),
            SummaryUpdating: WallRefreshProcessor.IsResummarizing(refresh, DateTimeOffset.UtcNow),
            LookOver: refresh.Status == WallRefreshStatus.Done ? await LookOverAsync(db, refresh.WallId) : null,
            FullRevision: refresh.FullRevision,
            Revision: refresh.Status == WallRefreshStatus.ReadyToStart ? await RevisionPreviewAsync(db, refresh.WallId) : null);
    }

    /// <summary>The wall's look-over flag with its open findings, or null while the wall is not flagged.</summary>
    private static async Task<RefreshLookOver?> LookOverAsync(BlocwerkDbContext db, Guid wallId)
    {
        var since = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.LookOverPendingSince).FirstOrDefaultAsync();
        if (since is not { } pendingSince)
        {
            return null;
        }

        var open = await db.WallLookOverFindings.CountAsync(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open);
        return new RefreshLookOver(pendingSince, open);
    }

    /// <summary>What a full revision started now would replace: the wall's current holds and its boulders not archived yet.</summary>
    private static async Task<RefreshRevisionPreview> RevisionPreviewAsync(BlocwerkDbContext db, Guid wallId)
    {
        var name = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.Name).FirstAsync();
        var holds = await (await LiveWallHolds.QueryAsync(db, wallId)).CountAsync(h => !h.IsVirtual);
        var boulders = await db.Boulders.CountAsync(b => b.WallId == wallId && !b.IsArchived);
        return new RefreshRevisionPreview(name, holds, boulders);
    }

    private async Task<bool> CaptureAvailableAsync(BlocwerkDbContext db, Guid wallId)
    {
        if (!captures.IsComputeConfigured)
        {
            return false;
        }

        var glyphs = await db.Walls.Where(w => w.Id == wallId).Select(w => w.GlyphsEnabled).FirstOrDefaultAsync();
        return glyphs || await captures.IsMarkerlessAvailableAsync();
    }
}
