using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface IBoulderService
{
    /// <summary>
    /// Creates a boulder. <paramref name="handsFollowFeet"/> and <paramref name="footColorOnly"/>
    /// are the boulder's foothold rules; a <see cref="HoldUsage.FootOnly"/> mark is coerced
    /// to <see cref="HoldUsage.HandAndFoot"/> while hands follow feet.
    /// <para>
    /// Pass a client-minted <paramref name="id"/> when the call may be replayed from an offline
    /// queue: creation becomes an idempotent upsert on that id, so a second call with the same id
    /// (from the same creator) returns the boulder already stored instead of inserting a duplicate.
    /// </para>
    /// </summary>
    Task<Boulder> CreateBoulderAsync(
        Guid wallId,
        string name,
        string? grade,
        List<BoulderHoldInput> holds,
        bool isDraft = false,
        bool kickboardFootholdsOn = true,
        bool handsFollowFeet = true,
        string? footColorOnly = null,
        Guid? id = null);

    /// <summary>
    /// Makes a draft visible to everyone on the wall.
    /// </summary>
    Task<Boulder> PublishBoulderAsync(Guid boulderId);

    Task<Boulder?> GetBoulderAsync(Guid boulderId);

    Task<Boulder?> GetBoulderByShareTokenAsync(Guid boulderId, string shareToken);

    Task<List<Boulder>> GetBouldersForWallAsync(Guid wallId, bool includeArchived = false);

    /// <summary>
    /// Which boulders currently use each hold on the wall, keyed by hold id. Holds that
    /// no boulder uses are absent from the map. Respects draft visibility: another
    /// member's unpublished draft never shows up.
    /// </summary>
    Task<Dictionary<Guid, List<HoldUsageRef>>> GetHoldUsageAsync(Guid wallId);

    /// <summary>
    /// Updates a boulder. Null rule arguments leave the stored rule untouched; pass an
    /// empty string for <paramref name="footColorOnly"/> to clear the foot color rule.
    /// </summary>
    Task<Boulder> UpdateBoulderAsync(
        Guid boulderId,
        string name,
        string? grade,
        List<BoulderHoldInput>? holds = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null);

    /// <summary>
    /// Remaps a historic or draft boulder onto the current hold model, optionally renaming
    /// and regrading it. Attempts, comments and grade proposals are preserved. Null rule
    /// arguments leave the stored rule untouched; pass an empty string for
    /// <paramref name="footColorOnly"/> to clear the foot color rule.
    /// </summary>
    Task<Boulder> ReviseBoulderAsync(
        Guid boulderId,
        List<BoulderHoldInput> updatedHolds,
        string? name = null,
        string? grade = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null);

    /// <summary>
    /// Renames and/or regrades a boulder in place, without touching its holds. Only the
    /// creator may do this, and it works on a live boulder (unlike <see cref="ReviseBoulderAsync"/>,
    /// which is for historic/draft remaps). Pass null or empty <paramref name="grade"/> to clear it.
    /// </summary>
    Task<Boulder> RenameBoulderAsync(Guid boulderId, string name, string? grade);

    Task DeleteBoulderAsync(Guid boulderId);

    /// <summary>
    /// Moves one of the creator's own historic boulders into the archive. Only the
    /// creator may archive, and only a historic boulder can be archived.
    /// </summary>
    Task ArchiveBoulderAsync(Guid boulderId);

    Task UnarchiveBoulderAsync(Guid boulderId);

    Task<GradeProposal> ProposeGradeAsync(Guid boulderId, string proposedGrade);

    Task<GradeProposal?> GetActiveProposalAsync(Guid boulderId);

    Task AcceptGradeProposalAsync(Guid proposalId);

    Task RejectGradeProposalAsync(Guid proposalId);
}

public record BoulderHoldInput(Guid HoldId, HoldType Type = HoldType.Normal, HoldUsage Usage = HoldUsage.HandAndFoot);

/// <summary>
/// One boulder's use of a hold, for the wall's "is this hold in use?" tool.
/// </summary>
public record HoldUsageRef(
    Guid BoulderId,
    string Name,
    string? Grade,
    HoldType Type,
    HoldUsage Usage,
    bool IsDraft,
    bool IsHistoric);

public class BoulderService : IBoulderService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<BoulderService> _logger;

    public BoulderService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService,
        ILogger<BoulderService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _activityLogService = activityLogService;
        _logger = logger;
    }

    public async Task<Boulder> CreateBoulderAsync(
        Guid wallId,
        string name,
        string? grade,
        List<BoulderHoldInput> holds,
        bool isDraft = false,
        bool kickboardFootholdsOn = true,
        bool handsFollowFeet = true,
        string? footColorOnly = null,
        Guid? id = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Create", wallId);
        try
        {
            holds = EnforceHandsFollowFeet(holds, handsFollowFeet);

            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Idempotent replay: a queued offline create that reaches the server twice must not
            // insert a second boulder. The client mints the id, so an existing row under that id is
            // the earlier apply of this very create; return it unchanged rather than re-inserting.
            if (id.HasValue)
            {
                var existing = await db.Boulders
                    .Include(b => b.BoulderHolds)
                    .FirstOrDefaultAsync(b => b.Id == id.Value);

                if (existing != null)
                {
                    if (existing.CreatedByUserId != user.Id)
                    {
                        // A client id colliding with another user's boulder is astronomically
                        // unlikely; surface it as not-found so the queue drops it permanently.
                        _logger.LogWarning(
                            "Create replay rejected: boulder {BoulderId} belongs to {OwnerUserId}, not caller {UserId}",
                            id.Value, existing.CreatedByUserId, user.Id);
                        throw new InvalidOperationException("Boulder not found");
                    }

                    return existing;
                }
            }

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Create boulder failed: wall {WallId} not found for user {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            var boulder = new Boulder
            {
                Id = id ?? Guid.NewGuid(),
                WallId = wallId,
                Name = name,
                Grade = grade,
                CreatedByUserId = user.Id,
                Generation = wall.CurrentGeneration,
                KickboardFootholdsOn = kickboardFootholdsOn,
                HandsFollowFeet = handsFollowFeet,
                FootColorOnly = NormalizeFootColor(footColorOnly),
                IsDraft = isDraft,
                PublishedAt = isDraft ? null : DateTimeOffset.UtcNow,
            };

            db.Boulders.Add(boulder);

            foreach (var h in holds)
            {
                db.BoulderHolds.Add(new BoulderHold
                {
                    BoulderId = boulder.Id,
                    HoldId = h.HoldId,
                    Type = h.Type,
                    Usage = h.Usage,
                });
            }

            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordBoulderCreated(wallId, isDraft);
            _logger.LogInformation(
                "Boulder {BoulderId} created on wall {WallId} by {UserId} (isDraft={IsDraft}, holds={HoldCount})",
                boulder.Id, wallId, user.Id, isDraft, holds.Count);

            // Drafts stay out of the activity feed until they are published.
            if (!isDraft)
            {
                await _activityLogService.LogAsync(wallId, boulder.Id, ActivityType.BoulderCreated, name);
            }

            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder> PublishBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Publish");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                .Include(b => b.BoulderHolds)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Publish failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            if (boulder.CreatedByUserId != user.Id)
            {
                _logger.LogWarning(
                    "Publish denied: user {UserId} is not creator {OwnerUserId} of boulder {BoulderId}",
                    user.Id, boulder.CreatedByUserId, boulderId);
                throw new InvalidOperationException("Only the creator can publish a boulder");
            }

            if (!boulder.IsDraft)
            {
                return boulder;
            }

            if (boulder.BoulderHolds.Count == 0)
            {
                _logger.LogWarning("Publish rejected: boulder {BoulderId} has no holds", boulderId);
                throw new InvalidOperationException("Select at least one hold before publishing");
            }

            boulder.IsDraft = false;
            boulder.PublishedAt = DateTimeOffset.UtcNow;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == boulder.WallId);
            if (wall != null)
            {
                boulder.Generation = wall.CurrentGeneration;
            }

            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Boulder {BoulderId} published on wall {WallId} by {UserId}",
                boulderId, boulder.WallId, user.Id);

            await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.BoulderCreated, boulder.Name);
            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// While hands follow feet the boulder has no dedicated footholds, so a
    /// <see cref="HoldUsage.FootOnly"/> mark is contradictory. It is coerced rather than
    /// rejected, so no client can push the boulder into an inconsistent state.
    /// </summary>
    private static List<BoulderHoldInput> EnforceHandsFollowFeet(List<BoulderHoldInput> holds, bool handsFollowFeet)
    {
        if (!handsFollowFeet)
        {
            return holds;
        }

        return holds
            .Select(h => h.Usage == HoldUsage.FootOnly ? h with { Usage = HoldUsage.HandAndFoot } : h)
            .ToList();
    }

    /// <summary>
    /// An empty foot color means "no foot color rule"; anything else is stored as given.
    /// </summary>
    private static string? NormalizeFootColor(string? footColorOnly) =>
        string.IsNullOrWhiteSpace(footColorOnly) ? null : footColorOnly;

    /// <summary>
    /// True when applying this revise would leave the boulder exactly as it already is, so the
    /// call is a no-op replay of an earlier apply. Only fields the caller actually supplied are
    /// compared, mirroring the "null argument leaves the stored value untouched" contract.
    /// </summary>
    private static bool RevisionIsNoOp(
        Boulder boulder,
        List<BoulderHoldInput> updatedHolds,
        string? name,
        string? grade,
        bool? kickboardFootholdsOn,
        bool? handsFollowFeet,
        string? footColorOnly)
    {
        var effectiveHandsFollowFeet = handsFollowFeet ?? boulder.HandsFollowFeet;
        var target = EnforceHandsFollowFeet(updatedHolds, effectiveHandsFollowFeet)
            .Select(h => (h.HoldId, h.Type, h.Usage))
            .ToHashSet();
        var current = boulder.BoulderHolds
            .Select(h => (h.HoldId, h.Type, h.Usage))
            .ToHashSet();

        if (!current.SetEquals(target))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(name) && boulder.Name != name)
        {
            return false;
        }

        if (grade != null && boulder.Grade != grade)
        {
            return false;
        }

        if (kickboardFootholdsOn.HasValue && boulder.KickboardFootholdsOn != kickboardFootholdsOn.Value)
        {
            return false;
        }

        if (handsFollowFeet.HasValue && boulder.HandsFollowFeet != handsFollowFeet.Value)
        {
            return false;
        }

        if (footColorOnly != null && boulder.FootColorOnly != NormalizeFootColor(footColorOnly))
        {
            return false;
        }

        return true;
    }

    public async Task<Boulder?> GetBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Get");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            return await db.Boulders
                // Two collection includes (holds + attempts) in one SQL statement is a cartesian
                // product: rows = holds x attempts, and it grows as attempts accumulate. Split it
                // into one query per collection instead (EF logs a MultipleCollectionInclude
                // warning otherwise). AsNoTracking: the context is disposed on return, so this is
                // a read-only projection for display and never needs change tracking.
                .AsSplitQuery()
                .AsNoTracking()
                .Include(b => b.BoulderHolds).ThenInclude(bh => bh.Hold)
                .Include(b => b.Attempts.OrderByDescending(a => a.Timestamp))
                .Include(b => b.CreatedBy)
                .Include(b => b.Wall)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder?> GetBoulderByShareTokenAsync(Guid boulderId, string shareToken)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetByShareToken");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            return await db.Boulders
                // See GetBoulderAsync: split the two collection includes to avoid a cartesian blow-up.
                .AsSplitQuery()
                .AsNoTracking()
                .Include(b => b.BoulderHolds).ThenInclude(bh => bh.Hold)
                .Include(b => b.Attempts.OrderByDescending(a => a.Timestamp)).ThenInclude(a => a.User)
                .Include(b => b.CreatedBy)
                .Include(b => b.Wall)
                .Where(b => b.Id == boulderId && b.Wall.ShareToken == shareToken && !b.IsDraft)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<Boulder>> GetBouldersForWallAsync(Guid wallId, bool includeArchived = false)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetForWall", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var query = db.Boulders
                // Split the two collection includes (holds + attempts) to avoid a cartesian blow-up.
                .AsSplitQuery()
                .AsNoTracking()
                .Include(b => b.BoulderHolds)
                .Include(b => b.Attempts)
                .Include(b => b.CreatedBy)
                .Where(b => b.WallId == wallId)
                .Where(b => !b.IsDraft || b.CreatedByUserId == user.Id);

            if (!includeArchived)
            {
                query = query.Where(b => !b.IsArchived);
            }

            return await query.OrderByDescending(b => b.CreatedAt).ToListAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Dictionary<Guid, List<HoldUsageRef>>> GetHoldUsageAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetHoldUsage", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var links = await db.BoulderHolds
                .AsNoTracking()
                .Where(bh => bh.Boulder.WallId == wallId && !bh.Boulder.IsArchived)
                .Where(bh => !bh.Boulder.IsDraft || bh.Boulder.CreatedByUserId == user.Id)
                .Select(bh => new
                {
                    bh.HoldId,
                    Ref = new HoldUsageRef(
                        bh.BoulderId,
                        bh.Boulder.Name,
                        bh.Boulder.Grade,
                        bh.Type,
                        bh.Usage,
                        bh.Boulder.IsDraft,
                        bh.Boulder.IsHistoric),
                })
                .ToListAsync();

            return links
                .GroupBy(x => x.HoldId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Ref).OrderBy(r => r.Name).ToList());
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder> UpdateBoulderAsync(
        Guid boulderId,
        string name,
        string? grade,
        List<BoulderHoldInput>? holds = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Update");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                .Include(b => b.BoulderHolds)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Update failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            boulder.Name = name;
            boulder.Grade = grade;

            if (kickboardFootholdsOn.HasValue)
            {
                boulder.KickboardFootholdsOn = kickboardFootholdsOn.Value;
            }

            if (handsFollowFeet.HasValue)
            {
                boulder.HandsFollowFeet = handsFollowFeet.Value;
            }

            if (footColorOnly != null)
            {
                boulder.FootColorOnly = NormalizeFootColor(footColorOnly);
            }

            if (holds != null)
            {
                db.BoulderHolds.RemoveRange(boulder.BoulderHolds);
                foreach (var h in EnforceHandsFollowFeet(holds, boulder.HandsFollowFeet))
                {
                    db.BoulderHolds.Add(new BoulderHold
                    {
                        BoulderId = boulderId,
                        HoldId = h.HoldId,
                        Type = h.Type,
                        Usage = h.Usage,
                    });
                }
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Boulder {BoulderId} updated by {UserId}", boulderId, user.Id);
            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder> RenameBoulderAsync(Guid boulderId, string name, string? grade)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Rename");
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _logger.LogWarning("Rename rejected: empty name for boulder {BoulderId}", boulderId);
                throw new InvalidOperationException("A boulder needs a name");
            }

            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Rename failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            if (boulder.CreatedByUserId != user.Id)
            {
                _logger.LogWarning(
                    "Rename denied: user {UserId} is not creator {OwnerUserId} of boulder {BoulderId}",
                    user.Id, boulder.CreatedByUserId, boulderId);
                throw new InvalidOperationException("Only the creator can edit this boulder");
            }

            boulder.Name = name.Trim();
            boulder.Grade = string.IsNullOrWhiteSpace(grade) ? null : grade.Trim();
            await db.SaveChangesAsync();
            _logger.LogInformation("Boulder {BoulderId} renamed by {UserId}", boulderId, user.Id);
            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Boulder> ReviseBoulderAsync(
        Guid boulderId,
        List<BoulderHoldInput> updatedHolds,
        string? name = null,
        string? grade = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Revise");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders
                .Include(b => b.BoulderHolds)
                .FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Revise failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            if (boulder.CreatedByUserId != user.Id)
            {
                _logger.LogWarning(
                    "Revise denied: user {UserId} is not creator {OwnerUserId} of boulder {BoulderId}",
                    user.Id, boulder.CreatedByUserId, boulderId);
                throw new InvalidOperationException("Only the creator can revise a boulder");
            }

            if (!boulder.IsHistoric && !boulder.IsDraft)
            {
                // A revise only applies to a historic or draft boulder. But an offline replay of an
                // already-applied revise arrives here after the first apply flipped IsHistoric off.
                // If the requested state already matches what is stored, this is that replay: return
                // the boulder unchanged so the queue records a success. A genuine attempt to revise a
                // live boulder would change something and is still rejected.
                if (RevisionIsNoOp(boulder, updatedHolds, name, grade, kickboardFootholdsOn, handsFollowFeet, footColorOnly))
                {
                    return boulder;
                }

                _logger.LogWarning("Revise rejected: boulder {BoulderId} is not historic", boulderId);
                throw new InvalidOperationException("Boulder is not historic");
            }

            if (updatedHolds.Count == 0 && !boulder.IsDraft)
            {
                _logger.LogWarning("Revise rejected: boulder {BoulderId} has no holds", boulderId);
                throw new InvalidOperationException("Select at least one hold");
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                boulder.Name = name;
            }

            if (grade != null)
            {
                boulder.Grade = grade;
            }

            if (kickboardFootholdsOn.HasValue)
            {
                boulder.KickboardFootholdsOn = kickboardFootholdsOn.Value;
            }

            if (handsFollowFeet.HasValue)
            {
                boulder.HandsFollowFeet = handsFollowFeet.Value;
            }

            if (footColorOnly != null)
            {
                boulder.FootColorOnly = NormalizeFootColor(footColorOnly);
            }

            db.BoulderHolds.RemoveRange(boulder.BoulderHolds);
            foreach (var h in EnforceHandsFollowFeet(updatedHolds, boulder.HandsFollowFeet))
            {
                db.BoulderHolds.Add(new BoulderHold
                {
                    BoulderId = boulderId,
                    HoldId = h.HoldId,
                    Type = h.Type,
                    Usage = h.Usage,
                });
            }

            // Remapping onto the current hold model resolves both staleness flags.
            boulder.IsHistoric = false;
            boulder.NeedsReview = false;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == boulder.WallId);
            if (wall != null)
            {
                boulder.Generation = wall.CurrentGeneration;
            }

            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Boulder {BoulderId} revised on wall {WallId} by {UserId}",
                boulderId, boulder.WallId, user.Id);

            await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.BoulderRevised);
            return boulder;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DeleteBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Delete");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Delete failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            db.Boulders.Remove(boulder);
            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordBoulderDeleted(boulder.WallId);
            _logger.LogInformation(
                "Boulder {BoulderId} deleted from wall {WallId} by {UserId}",
                boulderId, boulder.WallId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task ArchiveBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Archive");
        try
        {
            await SetArchivedAsync(boulderId, true);
            _logger.LogInformation("Boulder {BoulderId} archived", boulderId);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task UnarchiveBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.Unarchive");
        try
        {
            await SetArchivedAsync(boulderId, false);
            _logger.LogInformation("Boulder {BoulderId} unarchived", boulderId);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    private async Task SetArchivedAsync(Guid boulderId, bool archived)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        if (boulder.CreatedByUserId != user.Id)
        {
            throw new InvalidOperationException("Only the creator can archive a boulder");
        }

        if (archived && !boulder.IsHistoric)
        {
            throw new InvalidOperationException("Only historic boulders can be archived");
        }

        boulder.IsArchived = archived;
        await db.SaveChangesAsync();
    }

    public async Task<GradeProposal> ProposeGradeAsync(Guid boulderId, string proposedGrade)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.ProposeGrade");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder == null)
            {
                _logger.LogWarning("Propose grade failed: boulder {BoulderId} not found for user {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            if (boulder.CreatedByUserId == user.Id)
            {
                _logger.LogWarning(
                    "Propose grade denied: user {UserId} is the creator of boulder {BoulderId}",
                    user.Id, boulderId);
                throw new InvalidOperationException("Cannot propose a grade for your own boulder");
            }

            var existing = await db.GradeProposals
                .FirstOrDefaultAsync(gp => gp.BoulderId == boulderId && !gp.IsResolved);

            if (existing != null)
            {
                existing.ProposedGrade = proposedGrade;
                existing.ProposedByUserId = user.Id;
                existing.CreatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogInformation(
                    "Grade proposal {ProposalId} updated for boulder {BoulderId} by {UserId}",
                    existing.Id, boulderId, user.Id);
                existing.ProposedBy = user;
                return existing;
            }

            var proposal = new GradeProposal
            {
                BoulderId = boulderId,
                ProposedByUserId = user.Id,
                ProposedGrade = proposedGrade,
            };

            db.GradeProposals.Add(proposal);
            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Grade proposal {ProposalId} created for boulder {BoulderId} by {UserId}",
                proposal.Id, boulderId, user.Id);

            proposal.ProposedBy = user;
            return proposal;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<GradeProposal?> GetActiveProposalAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetActiveProposal");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            return await db.GradeProposals
                .Include(gp => gp.ProposedBy)
                .FirstOrDefaultAsync(gp => gp.BoulderId == boulderId && !gp.IsResolved);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task AcceptGradeProposalAsync(Guid proposalId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.AcceptGradeProposal");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var proposal = await db.GradeProposals
                .Include(gp => gp.Boulder)
                .FirstOrDefaultAsync(gp => gp.Id == proposalId && !gp.IsResolved);
            if (proposal == null)
            {
                _logger.LogWarning("Accept grade failed: proposal {ProposalId} not found for user {UserId}", proposalId, user.Id);
                throw new InvalidOperationException("Proposal not found");
            }

            if (proposal.Boulder.CreatedByUserId != user.Id)
            {
                _logger.LogWarning(
                    "Accept grade denied: user {UserId} is not creator {OwnerUserId} of boulder {BoulderId} (proposal {ProposalId})",
                    user.Id, proposal.Boulder.CreatedByUserId, proposal.BoulderId, proposalId);
                throw new InvalidOperationException("Only the creator can accept a grade proposal");
            }

            proposal.Boulder.Grade = proposal.ProposedGrade;
            proposal.IsResolved = true;
            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Grade proposal {ProposalId} accepted for boulder {BoulderId} by {UserId}",
                proposalId, proposal.BoulderId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task RejectGradeProposalAsync(Guid proposalId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.RejectGradeProposal");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var proposal = await db.GradeProposals
                .Include(gp => gp.Boulder)
                .FirstOrDefaultAsync(gp => gp.Id == proposalId && !gp.IsResolved);
            if (proposal == null)
            {
                _logger.LogWarning("Reject grade failed: proposal {ProposalId} not found for user {UserId}", proposalId, user.Id);
                throw new InvalidOperationException("Proposal not found");
            }

            if (proposal.Boulder.CreatedByUserId != user.Id)
            {
                _logger.LogWarning(
                    "Reject grade denied: user {UserId} is not creator {OwnerUserId} of boulder {BoulderId} (proposal {ProposalId})",
                    user.Id, proposal.Boulder.CreatedByUserId, proposal.BoulderId, proposalId);
                throw new InvalidOperationException("Only the creator can reject a grade proposal");
            }

            proposal.IsResolved = true;
            await db.SaveChangesAsync();
            _logger.LogInformation(
                "Grade proposal {ProposalId} rejected for boulder {BoulderId} by {UserId}",
                proposalId, proposal.BoulderId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }
}
