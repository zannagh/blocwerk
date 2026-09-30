// <copyright file="WallRefreshService.View.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
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
            refresh.UpdateSessionId);
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
