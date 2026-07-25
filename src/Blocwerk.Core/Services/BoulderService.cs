using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface IBoulderService
{
    Task<Boulder> CreateBoulderAsync(Guid wallId, string name, string? grade, List<BoulderHoldInput> holds, bool isDraft = false, bool kickboardFootholdsOn = true);

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

    Task<Boulder> UpdateBoulderAsync(Guid boulderId, string name, string? grade, List<BoulderHoldInput>? holds = null, bool? kickboardFootholdsOn = null);

    /// <summary>
    /// Remaps a historic or draft boulder onto the current hold model, optionally renaming
    /// and regrading it. Attempts, comments and grade proposals are preserved.
    /// </summary>
    Task<Boulder> ReviseBoulderAsync(Guid boulderId, List<BoulderHoldInput> updatedHolds, string? name = null, string? grade = null, bool? kickboardFootholdsOn = null);

    Task DeleteBoulderAsync(Guid boulderId);

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

    public async Task<Boulder> CreateBoulderAsync(Guid wallId, string name, string? grade, List<BoulderHoldInput> holds, bool isDraft = false, bool kickboardFootholdsOn = true)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        var boulder = new Boulder
        {
            WallId = wallId,
            Name = name,
            Grade = grade,
            CreatedByUserId = user.Id,
            Generation = wall.CurrentGeneration,
            FootholdMode = DeriveFootholdMode(holds),
            KickboardFootholdsOn = kickboardFootholdsOn,
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
    /// A boulder defines its footholds explicitly as soon as any hold carries a
    /// non-default usage mark, so the setter never picks the mode by hand.
    /// </summary>
    private static FootholdMode DeriveFootholdMode(IEnumerable<BoulderHoldInput> holds) =>
        holds.Any(h => h.Usage != HoldUsage.HandAndFoot)
            ? FootholdMode.DefinedOnly
            : FootholdMode.AllKickboard;

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

    public async Task<Boulder> UpdateBoulderAsync(Guid boulderId, string name, string? grade, List<BoulderHoldInput>? holds = null, bool? kickboardFootholdsOn = null)
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

        if (holds != null)
        {
            db.BoulderHolds.RemoveRange(boulder.BoulderHolds);
            foreach (var h in holds)
            {
                db.BoulderHolds.Add(new BoulderHold
                {
                    BoulderId = boulderId,
                    HoldId = h.HoldId,
                    Type = h.Type,
                    Usage = h.Usage,
                });
            }

            boulder.FootholdMode = DeriveFootholdMode(holds);
        }

        await db.SaveChangesAsync();
        return boulder;
    }

    public async Task<Boulder> ReviseBoulderAsync(Guid boulderId, List<BoulderHoldInput> updatedHolds, string? name = null, string? grade = null, bool? kickboardFootholdsOn = null)
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
            throw new InvalidOperationException("Boulder is not historic");
        }

        if (updatedHolds.Count == 0 && !boulder.IsDraft)
        {
            throw new InvalidOperationException("Select at least one hold");
        }

        db.BoulderHolds.RemoveRange(boulder.BoulderHolds);
        foreach (var h in updatedHolds)
        {
            db.BoulderHolds.Add(new BoulderHold
            {
                BoulderId = boulderId,
                HoldId = h.HoldId,
                Type = h.Type,
                Usage = h.Usage,
            });
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

        boulder.FootholdMode = DeriveFootholdMode(updatedHolds);

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
