using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface IBoulderFeedbackService
{
    /// <summary>
    /// Sets or updates the current user's star rating (1-5) for a boulder.
    /// </summary>
    Task SetRatingAsync(Guid boulderId, int stars);

    /// <summary>
    /// Clears the current user's rating for a boulder, if any.
    /// </summary>
    Task RemoveRatingAsync(Guid boulderId);

    Task<RatingInfo> GetRatingAsync(Guid boulderId);

    /// <summary>
    /// Toggles the current user's favorite mark for a boulder and returns the new state.
    /// </summary>
    Task<bool> ToggleFavoriteAsync(Guid boulderId);

    /// <summary>
    /// Sets the current user's favorite mark to an absolute value and returns it. Unlike
    /// <see cref="ToggleFavoriteAsync"/> this is idempotent, so it is the call an offline
    /// queue replays: applying the same desired state twice is a no-op.
    /// </summary>
    Task<bool> SetFavoriteAsync(Guid boulderId, bool favorite);

    Task<bool> IsFavoritedAsync(Guid boulderId);

    /// <summary>
    /// Every boulder on the wall (including archived and the user's own drafts) with the
    /// per-user attempt, favorite and rating data the overview page needs, in one pass.
    /// </summary>
    Task<List<BoulderListItem>> GetBoulderListAsync(Guid wallId);
}

public record RatingInfo(double? Average, int Count, int? MyRating);

public record BoulderListItem(
    Boulder Boulder,
    int MyAttemptCount,
    bool HasSent,
    bool HasFlashed,
    bool IsFavorite,
    double? AverageRating,
    int RatingCount,
    int? MyRating)
{
    public bool DoneByMe => HasSent || HasFlashed;

    public bool AttemptedByMe => MyAttemptCount > 0;
}

public class BoulderFeedbackService : IBoulderFeedbackService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<BoulderFeedbackService> _logger;

    public BoulderFeedbackService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        ILogger<BoulderFeedbackService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    public async Task SetRatingAsync(Guid boulderId, int stars)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderFeedback.SetRating");
        try
        {
            if (stars is < 1 or > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(stars), "Rating must be between 1 and 5 stars");
            }

            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            await EnsureMemberAsync(db, boulderId, user.Id);

            var existing = await db.BoulderRatings
                .FirstOrDefaultAsync(r => r.BoulderId == boulderId && r.UserId == user.Id);

            if (existing == null)
            {
                db.BoulderRatings.Add(new BoulderRating
                {
                    BoulderId = boulderId,
                    UserId = user.Id,
                    Stars = stars,
                });
            }
            else
            {
                existing.Stars = stars;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Rating set to {Stars} on boulder {BoulderId} by {UserId}", stars, boulderId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task RemoveRatingAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderFeedback.RemoveRating");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var existing = await db.BoulderRatings
                .FirstOrDefaultAsync(r => r.BoulderId == boulderId && r.UserId == user.Id);

            if (existing != null)
            {
                db.BoulderRatings.Remove(existing);
                await db.SaveChangesAsync();
                _logger.LogInformation("Rating removed on boulder {BoulderId} by {UserId}", boulderId, user.Id);
            }
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<RatingInfo> GetRatingAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderFeedback.GetRating");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var ratings = await db.BoulderRatings
                .Where(r => r.BoulderId == boulderId)
                .Select(r => new { r.UserId, r.Stars })
                .ToListAsync();

            var average = ratings.Count > 0 ? ratings.Average(r => (double)r.Stars) : (double?)null;
            var mine = ratings.FirstOrDefault(r => r.UserId == user.Id)?.Stars;

            return new RatingInfo(average, ratings.Count, mine);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<bool> ToggleFavoriteAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderFeedback.ToggleFavorite");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            await EnsureMemberAsync(db, boulderId, user.Id);

            var isFavorite = await db.BoulderFavorites
                .AnyAsync(f => f.BoulderId == boulderId && f.UserId == user.Id);

            var result = await SetFavoriteAsync(boulderId, !isFavorite);
            _logger.LogInformation("Favorite toggled to {Favorite} on boulder {BoulderId} by {UserId}", result, boulderId, user.Id);
            return result;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<bool> SetFavoriteAsync(Guid boulderId, bool favorite)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderFeedback.SetFavorite");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            await EnsureMemberAsync(db, boulderId, user.Id);

            var existing = await db.BoulderFavorites
                .FirstOrDefaultAsync(f => f.BoulderId == boulderId && f.UserId == user.Id);

            if (favorite && existing == null)
            {
                db.BoulderFavorites.Add(new BoulderFavorite
                {
                    BoulderId = boulderId,
                    UserId = user.Id,
                });
                await db.SaveChangesAsync();
                _logger.LogInformation("Favorite set to {Favorite} on boulder {BoulderId} by {UserId}", favorite, boulderId, user.Id);
            }
            else if (!favorite && existing != null)
            {
                db.BoulderFavorites.Remove(existing);
                await db.SaveChangesAsync();
                _logger.LogInformation("Favorite set to {Favorite} on boulder {BoulderId} by {UserId}", favorite, boulderId, user.Id);
            }

            return favorite;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<bool> IsFavoritedAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderFeedback.IsFavorited");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            return await db.BoulderFavorites
                .AnyAsync(f => f.BoulderId == boulderId && f.UserId == user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<BoulderListItem>> GetBoulderListAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BoulderFeedback.GetBoulderList", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Read-only list for the wall overview. Two collection includes (holds + setters), so
            // split the query to avoid a cartesian blow-up; AsNoTracking as it is display-only.
            var boulders = await db.Boulders
                .AsSplitQuery()
                .AsNoTracking()
                .Include(b => b.BoulderHolds)
                .Include(b => b.Setters)
                .Include(b => b.CreatedBy)

                // Drafts are visible to every wall member (they just can't be logged until published),
                // so no creator-only draft filter here.
                .Where(b => b.WallId == wallId)
                .ToListAsync();

            var ids = boulders.Select(b => b.Id).ToList();

            var myAttempts = await db.Attempts
                .Where(a => ids.Contains(a.BoulderId) && a.UserId == user.Id)
                .Select(a => new { a.BoulderId, a.Type })
                .ToListAsync();

            var myFavorites = (await db.BoulderFavorites
                    .Where(f => ids.Contains(f.BoulderId) && f.UserId == user.Id)
                    .Select(f => f.BoulderId)
                    .ToListAsync())
                .ToHashSet();

            var ratings = await db.BoulderRatings
                .Where(r => ids.Contains(r.BoulderId))
                .Select(r => new { r.BoulderId, r.UserId, r.Stars })
                .ToListAsync();

            var attemptsByBoulder = myAttempts
                .GroupBy(a => a.BoulderId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var ratingsByBoulder = ratings
                .GroupBy(r => r.BoulderId)
                .ToDictionary(g => g.Key, g => g.ToList());

            return boulders.Select(b =>
            {
                var att = attemptsByBoulder.GetValueOrDefault(b.Id);
                var rs = ratingsByBoulder.GetValueOrDefault(b.Id);
                return new BoulderListItem(
                    b,
                    MyAttemptCount: att?.Count ?? 0,
                    HasSent: att?.Any(a => a.Type == AttemptType.Send) ?? false,
                    HasFlashed: att?.Any(a => a.Type == AttemptType.Flash) ?? false,
                    IsFavorite: myFavorites.Contains(b.Id),
                    AverageRating: rs is { Count: > 0 } ? rs.Average(r => (double)r.Stars) : null,
                    RatingCount: rs?.Count ?? 0,
                    MyRating: rs?.FirstOrDefault(r => r.UserId == user.Id)?.Stars);
            }).ToList();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Ratings and favorites are for wall members only; the boulder detail UI already
    /// hides the controls from guests, but the service guards the write path too.
    /// </summary>
    private static async Task EnsureMemberAsync(BlocwerkDbContext db, Guid boulderId, Guid userId)
    {
        var wallId = await db.Boulders
            .Where(b => b.Id == boulderId)
            .Select(b => (Guid?)b.WallId)
            .FirstOrDefaultAsync();

        if (wallId == null)
        {
            throw new InvalidOperationException("Boulder not found");
        }

        var isMember = await db.WallMembers
            .AnyAsync(m => m.WallId == wallId.Value && m.UserId == userId);

        if (!isMember)
        {
            throw new InvalidOperationException("Only wall members can do this");
        }
    }
}
