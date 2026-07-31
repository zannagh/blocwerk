using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

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

    public BoulderService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _activityLogService = activityLogService;
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
                    throw new InvalidOperationException("Boulder not found");
                }

                return existing;
            }
        }

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

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

        // Drafts stay out of the activity feed until they are published.
        if (!isDraft)
        {
            await _activityLogService.LogAsync(wallId, boulder.Id, ActivityType.BoulderCreated, name);
        }

        return boulder;
    }

    public async Task<Boulder> PublishBoulderAsync(Guid boulderId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders
                          .Include(b => b.BoulderHolds)
                          .FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        if (boulder.CreatedByUserId != user.Id)
        {
            throw new InvalidOperationException("Only the creator can publish a boulder");
        }

        if (!boulder.IsDraft)
        {
            return boulder;
        }

        if (boulder.BoulderHolds.Count == 0)
        {
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

        await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.BoulderCreated, boulder.Name);
        return boulder;
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
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        return await db.Boulders
            .Include(b => b.BoulderHolds).ThenInclude(bh => bh.Hold)
            .Include(b => b.Attempts.OrderByDescending(a => a.Timestamp))
            .Include(b => b.CreatedBy)
            .Include(b => b.Wall)
            .FirstOrDefaultAsync(b => b.Id == boulderId);
    }

    public async Task<Boulder?> GetBoulderByShareTokenAsync(Guid boulderId, string shareToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        return await db.Boulders
            .Include(b => b.BoulderHolds).ThenInclude(bh => bh.Hold)
            .Include(b => b.Attempts.OrderByDescending(a => a.Timestamp)).ThenInclude(a => a.User)
            .Include(b => b.CreatedBy)
            .Include(b => b.Wall)
            .Where(b => b.Id == boulderId && b.Wall.ShareToken == shareToken && !b.IsDraft)
            .FirstOrDefaultAsync();
    }

    public async Task<List<Boulder>> GetBouldersForWallAsync(Guid wallId, bool includeArchived = false)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var query = db.Boulders
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

    public async Task<Dictionary<Guid, List<HoldUsageRef>>> GetHoldUsageAsync(Guid wallId)
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

    public async Task<Boulder> UpdateBoulderAsync(
        Guid boulderId,
        string name,
        string? grade,
        List<BoulderHoldInput>? holds = null,
        bool? kickboardFootholdsOn = null,
        bool? handsFollowFeet = null,
        string? footColorOnly = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders
                          .Include(b => b.BoulderHolds)
                          .FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

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
        return boulder;
    }

    public async Task<Boulder> RenameBoulderAsync(Guid boulderId, string name, string? grade)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("A boulder needs a name");
        }

        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        if (boulder.CreatedByUserId != user.Id)
        {
            throw new InvalidOperationException("Only the creator can edit this boulder");
        }

        boulder.Name = name.Trim();
        boulder.Grade = string.IsNullOrWhiteSpace(grade) ? null : grade.Trim();
        await db.SaveChangesAsync();
        return boulder;
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
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders
                          .Include(b => b.BoulderHolds)
                          .FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        if (boulder.CreatedByUserId != user.Id)
        {
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

            throw new InvalidOperationException("Boulder is not historic");
        }

        if (updatedHolds.Count == 0 && !boulder.IsDraft)
        {
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

        await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.BoulderRevised);
        return boulder;
    }

    public async Task DeleteBoulderAsync(Guid boulderId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        db.Boulders.Remove(boulder);
        await db.SaveChangesAsync();
    }

    public async Task ArchiveBoulderAsync(Guid boulderId)
    {
        await SetArchivedAsync(boulderId, true);
    }

    public async Task UnarchiveBoulderAsync(Guid boulderId)
    {
        await SetArchivedAsync(boulderId, false);
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
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        if (boulder.CreatedByUserId == user.Id)
        {
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

        proposal.ProposedBy = user;
        return proposal;
    }

    public async Task<GradeProposal?> GetActiveProposalAsync(Guid boulderId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        return await db.GradeProposals
            .Include(gp => gp.ProposedBy)
            .FirstOrDefaultAsync(gp => gp.BoulderId == boulderId && !gp.IsResolved);
    }

    public async Task AcceptGradeProposalAsync(Guid proposalId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var proposal = await db.GradeProposals
                           .Include(gp => gp.Boulder)
                           .FirstOrDefaultAsync(gp => gp.Id == proposalId && !gp.IsResolved)
                       ?? throw new InvalidOperationException("Proposal not found");

        if (proposal.Boulder.CreatedByUserId != user.Id)
        {
            throw new InvalidOperationException("Only the creator can accept a grade proposal");
        }

        proposal.Boulder.Grade = proposal.ProposedGrade;
        proposal.IsResolved = true;
        await db.SaveChangesAsync();
    }

    public async Task RejectGradeProposalAsync(Guid proposalId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var proposal = await db.GradeProposals
                           .Include(gp => gp.Boulder)
                           .FirstOrDefaultAsync(gp => gp.Id == proposalId && !gp.IsResolved)
                       ?? throw new InvalidOperationException("Proposal not found");

        if (proposal.Boulder.CreatedByUserId != user.Id)
        {
            throw new InvalidOperationException("Only the creator can reject a grade proposal");
        }

        proposal.IsResolved = true;
        await db.SaveChangesAsync();
    }
}
