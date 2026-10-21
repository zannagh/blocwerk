// <copyright file="UpdateReviewService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The one review of a prepared wall update, see <see cref="IUpdateReviewService"/>. Reads the session's facts in one go
/// and works the items out with <see cref="UpdateReviewBuilder"/>; answers go through the session service (cards, "may
/// have moved") or one transaction of its own (not found, new, left out), always on the run's own session.
/// </summary>
public sealed partial class UpdateReviewService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IWallUpdateSessionService sessions,
    WallRefreshQueue queue,
    ICaptureFileStore files,
    MatchedSessionCache? matchCache = null,
    IKioskContext? kioskContext = null) : IUpdateReviewService
{
    private const string AdminAction = "Updating the wall";

    public async Task<UpdateReview?> GetAsync(Guid refreshId)
    {
        var (db, refresh, _) = await OpenAsync(refreshId);
        await using (db)
        {
            return await ReadAsync(db, refresh);
        }
    }

    private async Task<UpdateReview?> ReadAsync(BlocwerkDbContext db, WallRefresh refresh)
    {
        if (refresh.Status != WallRefreshStatus.ReadyToApply || refresh.UpdateSessionId is not { } sessionId)
        {
            return null;
        }

        var facts = await ReadFactsAsync(db, refresh.WallId, sessionId);
        if (facts is null)
        {
            return null;
        }

        var items = UpdateReviewBuilder.Build(facts);
        return new UpdateReview(
            refresh.Id,
            sessionId,
            RefreshTimeline.Summary(refresh)?.DecisionsVersion,
            WallRefreshProcessor.IsResummarizing(refresh, DateTimeOffset.UtcNow),
            facts.Panels,
            items,
            ReviewCounts.Of(items));
    }

    /// <summary>Opens the run for a wall admin (not from a kiosk tablet): the context, the run, and the user.</summary>
    private async Task<(BlocwerkDbContext Db, WallRefresh Refresh, Guid UserId)> OpenAsync(Guid refreshId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync();
        try
        {
            db.CurrentUserId = user.Id;
            var refresh = await db.WallRefreshes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == refreshId)
                ?? throw new UserFacingException("This update no longer exists.");
            KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
            await WallAdminGuard.EnsureWallAdminAsync(db, refresh.WallId, user.Id, CancellationToken.None);
            return (db, refresh, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
