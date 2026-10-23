using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface ICommentService
{
    /// <summary>
    /// Adds a comment. Pass a stable <paramref name="clientRequestId"/> when the call may
    /// be replayed from an offline queue: a second call with the same id returns the
    /// comment already stored instead of posting it twice.
    /// </summary>
    /// <remarks>Only a member of the boulder's wall may comment (a kiosk acting as a member included).</remarks>
    Task<BoulderComment> AddCommentAsync(Guid boulderId, string text, Guid? clientRequestId = null);

    /// <summary>
    /// A page of the boulder's comments, under the same visibility as the boulder itself: a member (or a kiosk
    /// acting as one), the wall's <paramref name="shareToken"/> for a published boulder, or the anonymous kiosk on
    /// its own wall for a published, unarchived boulder. Anyone else gets an empty page, as for a boulder without comments.
    /// </summary>
    Task<(List<BoulderComment> Items, int TotalCount)> GetCommentsAsync(Guid boulderId, int page = 0, int pageSize = 10, string? shareToken = null);

    /// <summary>Deletes one of the caller's own comments, on a wall the caller can still see.</summary>
    Task DeleteCommentAsync(Guid commentId);
}

public class CommentService : ICommentService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<CommentService> _logger;
    private readonly IPushNotificationService? _pushNotificationService;
    private readonly IKioskContext? _kioskContext;

    public CommentService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService,
        ILogger<CommentService> logger,
        IPushNotificationService? pushNotificationService = null,
        IKioskContext? kioskContext = null)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _activityLogService = activityLogService;
        _logger = logger;
        _pushNotificationService = pushNotificationService;
        _kioskContext = kioskContext;
    }

    public async Task<BoulderComment> AddCommentAsync(Guid boulderId, string text, Guid? clientRequestId = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Comment.Add");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Boulder carries no query filter of its own, so the id alone used to let ANY signed-in caller comment on
            // ANY boulder in the installation. Reaching through to db.Walls puts the write under the membership filter
            // (and the kiosk wall gate). A non-member gets the same not-found as a missing boulder, so no oracle.
            var boulder = await db.Boulders
                .Where(b => db.Walls.Any(w => w.Id == b.WallId))
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder is null)
            {
                _logger.LogWarning("Cannot add comment: boulder {BoulderId} not found for {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            if (clientRequestId.HasValue)
            {
                var replayed = await db.BoulderComments
                    .Include(c => c.User)
                    .FirstOrDefaultAsync(c => c.ClientRequestId == clientRequestId.Value);

                if (replayed != null)
                {
                    return replayed;
                }
            }

            var comment = new BoulderComment
            {
                BoulderId = boulderId,
                UserId = user.Id,
                Text = text,
                ClientRequestId = clientRequestId,
            };

            db.BoulderComments.Add(comment);
            await db.SaveChangesAsync();

            BlocwerkMetrics.RecordCommentAdded(boulder.WallId);

            _logger.LogInformation("Comment {CommentId} added on boulder {BoulderId} by {UserId}", comment.Id, boulderId, user.Id);

            await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.CommentAdded);

            // After the commit: tell the boulder's setters + creator. Guarded internally, so it can
            // never break or block adding the comment.
            if (_pushNotificationService is not null)
            {
                await _pushNotificationService.NotifyCommentAsync(boulderId, user.Id);
            }

            comment.User = user;
            return comment;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<(List<BoulderComment> Items, int TotalCount)> GetCommentsAsync(
        Guid boulderId, int page = 0, int pageSize = 10, string? shareToken = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Comment.Get");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var access = await CanReadBoulderAsync(db, boulderId, shareToken);
            if (access is null)
            {
                return ([], 0);
            }

            var query = db.BoulderComments
                .AsNoTracking()
                .Include(c => c.User)
                .Where(c => c.BoulderId == boulderId)
                .OrderByDescending(c => c.CreatedAt);

            var total = await query.CountAsync();
            var items = await query.Skip(page * pageSize).Take(pageSize).ToListAsync();
            if (access is { } kioskWall && kioskWall != Guid.Empty)
            {
                var consented = await KioskBylines.ConsentedAsync(db, kioskWall);
                foreach (var comment in items.Where(c => !consented.Contains(c.UserId)))
                {
                    comment.User = KioskBylines.Mask(comment.User, named: false);
                    comment.UserId = comment.User.Id == Guid.Empty ? Guid.Empty : comment.UserId;
                }
            }

            return (items, total);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DeleteCommentAsync(Guid commentId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Comment.Delete");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Own comments only, and only on a wall the caller can still see: a kiosk acting as a member stays on
            // the tablet's wall, as every other kiosk write does.
            var comment = await db.BoulderComments
                .Where(c => db.Walls.Any(w => w.Id == c.Boulder.WallId))
                .FirstOrDefaultAsync(c => c.Id == commentId && c.UserId == user.Id);
            if (comment is null)
            {
                _logger.LogWarning("Cannot delete comment {CommentId}: not found for {UserId}", commentId, user.Id);
                throw new InvalidOperationException("Comment not found");
            }

            db.BoulderComments.Remove(comment);
            await db.SaveChangesAsync();

            _logger.LogInformation("Comment {CommentId} deleted by {UserId}", commentId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Whether this caller may read the boulder, with the context's filter set for that answer: null when not, the
    /// boulder's wall for the resting kiosk (whose bylines <see cref="KioskBylines"/> masks), and
    /// <see cref="Guid.Empty"/> for everyone else who may. Mirrors
    /// <see cref="BoulderService.GetBoulderAsync"/> and <see cref="BoulderService.GetBoulderByShareTokenAsync"/>:
    /// the share token opens a published boulder of its own wall; otherwise the wall filter decides, with the
    /// anonymous kiosk (<see cref="Guid.Empty"/>) kept to published, unarchived boulders; any other anonymous caller may not.
    /// </summary>
    private async Task<Guid?> CanReadBoulderAsync(BlocwerkDbContext db, Guid boulderId, string? shareToken)
    {
        if (!string.IsNullOrEmpty(shareToken))
        {
            db.CurrentUserId = Guid.Empty;
            return await db.Boulders.AnyAsync(b => b.Id == boulderId && b.Wall.ShareToken == shareToken && !b.IsDraft)
                ? Guid.Empty
                : null;
        }

        Guid viewerId;
        try
        {
            viewerId = (await _currentUserService.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException) when (KioskViewing.ViewableWallId(_kioskContext) is not null)
        {
            viewerId = Guid.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        // The resting kiosk reads a boulder's comments the way it reads its activity
        // (KioskActivityVisibility): not a draft's, and not an archived one's.
        db.CurrentUserId = viewerId;
        var wallId = await db.Boulders
            .Where(b => b.Id == boulderId
                && db.Walls.Any(w => w.Id == b.WallId)
                && (viewerId != Guid.Empty || (!b.IsDraft && !b.IsArchived)))
            .Select(b => (Guid?)b.WallId)
            .FirstOrDefaultAsync();
        return wallId is null ? null : viewerId == Guid.Empty ? wallId : Guid.Empty;
    }
}
