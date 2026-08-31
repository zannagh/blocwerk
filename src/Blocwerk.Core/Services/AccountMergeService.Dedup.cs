using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Composite-primary-key dedup for the merge. These tables key on {..., UserId}, so a blind UserId
/// update would collide with a row the target already owns. Each helper re-points the source's row
/// only when the target has no equivalent, and otherwise drops the source's row (folding its value in
/// where the row carries one, e.g. the higher wall role). A key column can't be mutated on a tracked
/// entity, so a re-point is a remove-then-insert of a copy under the target's key.
/// </summary>
public partial class AccountMergeService
{
    private static async Task DedupWallMembersAsync(BlocwerkDbContext db, Guid sourceUserId, Guid targetUserId)
    {
        var sourceMembers = await db.WallMembers
            .Where(m => m.UserId == sourceUserId)
            .ToListAsync();
        var targetMembers = await db.WallMembers
            .Where(m => m.UserId == targetUserId)
            .ToListAsync();

        foreach (var sourceMember in sourceMembers)
        {
            var targetMember = targetMembers.FirstOrDefault(m => m.WallId == sourceMember.WallId);
            if (targetMember is not null)
            {
                // Already a member of this wall on both sides: keep the more-capable role, drop the
                // source's. Compare by capability precedence (Admin > Moderator > Member), NOT the
                // stored numeric value — WallRole is Member=0, Admin=1, Moderator=2, so a numeric
                // comparison would wrongly rank Moderator above Admin and demote the admin.
                if (WallRoleRank(sourceMember.Role) > WallRoleRank(targetMember.Role))
                {
                    targetMember.Role = sourceMember.Role;
                }

                db.WallMembers.Remove(sourceMember);
            }
            else
            {
                // Only the source is a member: hand the membership to the target.
                db.WallMembers.Remove(sourceMember);
                db.WallMembers.Add(new WallMember
                {
                    UserId = targetUserId,
                    WallId = sourceMember.WallId,
                    Role = sourceMember.Role,
                    JoinedAt = sourceMember.JoinedAt,
                });
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Capability precedence for a wall role: Admin (2) &gt; Moderator (1) &gt; Member (0). This is
    /// deliberately independent of the stored enum values (Member=0, Admin=1, Moderator=2) so role
    /// comparisons follow capability, not numeric order.
    /// </summary>
    private static int WallRoleRank(WallRole role) => role switch
    {
        WallRole.Admin => 2,
        WallRole.Moderator => 1,
        _ => 0,
    };

    private static async Task DedupBoulderRatingsAsync(BlocwerkDbContext db, Guid sourceUserId, Guid targetUserId)
    {
        var sourceRatings = await db.BoulderRatings
            .Where(r => r.UserId == sourceUserId)
            .ToListAsync();
        var targetRatedBoulders = (await db.BoulderRatings
                .Where(r => r.UserId == targetUserId)
                .Select(r => r.BoulderId)
                .ToListAsync())
            .ToHashSet();

        foreach (var sourceRating in sourceRatings)
        {
            db.BoulderRatings.Remove(sourceRating);

            // Target already rated this boulder: its own rating wins, so drop the source's.
            if (targetRatedBoulders.Contains(sourceRating.BoulderId))
            {
                continue;
            }

            db.BoulderRatings.Add(new BoulderRating
            {
                BoulderId = sourceRating.BoulderId,
                UserId = targetUserId,
                Stars = sourceRating.Stars,
                CreatedAt = sourceRating.CreatedAt,
                UpdatedAt = sourceRating.UpdatedAt,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task DedupBoulderFavoritesAsync(BlocwerkDbContext db, Guid sourceUserId, Guid targetUserId)
    {
        var sourceFavorites = await db.BoulderFavorites
            .Where(f => f.UserId == sourceUserId)
            .ToListAsync();
        var targetFavoritedBoulders = (await db.BoulderFavorites
                .Where(f => f.UserId == targetUserId)
                .Select(f => f.BoulderId)
                .ToListAsync())
            .ToHashSet();

        foreach (var sourceFavorite in sourceFavorites)
        {
            db.BoulderFavorites.Remove(sourceFavorite);

            // Target already favorited this boulder: a single favorite row is enough, drop the source's.
            if (targetFavoritedBoulders.Contains(sourceFavorite.BoulderId))
            {
                continue;
            }

            db.BoulderFavorites.Add(new BoulderFavorite
            {
                BoulderId = sourceFavorite.BoulderId,
                UserId = targetUserId,
                CreatedAt = sourceFavorite.CreatedAt,
            });
        }

        await db.SaveChangesAsync();
    }
}
