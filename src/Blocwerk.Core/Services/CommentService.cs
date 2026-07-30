using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface ICommentService
{
    /// <summary>
    /// Adds a comment. Pass a stable <paramref name="clientRequestId"/> when the call may
    /// be replayed from an offline queue: a second call with the same id returns the
    /// comment already stored instead of posting it twice.
    /// </summary>
    Task<BoulderComment> AddCommentAsync(Guid boulderId, string text, Guid? clientRequestId = null);

    Task<(List<BoulderComment> Items, int TotalCount)> GetCommentsAsync(Guid boulderId, int page = 0, int pageSize = 10);

    Task DeleteCommentAsync(Guid commentId);
}

public class CommentService : ICommentService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IActivityLogService _activityLogService;

    public CommentService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _activityLogService = activityLogService;
    }

    public async Task<BoulderComment> AddCommentAsync(Guid boulderId, string text, Guid? clientRequestId = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

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

        await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.CommentAdded);

        comment.User = user;
        return comment;
    }

    public async Task<(List<BoulderComment> Items, int TotalCount)> GetCommentsAsync(Guid boulderId, int page = 0, int pageSize = 10)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var query = db.BoulderComments
            .Include(c => c.User)
            .Where(c => c.BoulderId == boulderId)
            .OrderByDescending(c => c.CreatedAt);

        var total = await query.CountAsync();
        var items = await query.Skip(page * pageSize).Take(pageSize).ToListAsync();

        return (items, total);
    }

    public async Task DeleteCommentAsync(Guid commentId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var comment = await db.BoulderComments.FirstOrDefaultAsync(c => c.Id == commentId && c.UserId == user.Id)
                      ?? throw new InvalidOperationException("Comment not found");

        db.BoulderComments.Remove(comment);
        await db.SaveChangesAsync();
    }
}
