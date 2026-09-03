using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The wall-ownership half of <see cref="AccountDeletionService"/>: who a departing owner's walls go
/// to, and the check that none of them can end up owned by somebody who is themselves gone.
/// </summary>
public partial class AccountDeletionService
{
    /// <summary>
    /// Decides what happens to every wall the user owns.
    /// </summary>
    /// <remarks>
    /// The rule: a wall goes to its longest-standing remaining <see cref="WallRole.Admin"/> member,
    /// and if it has none the deletion is REFUSED and names the wall. Refusing is the only defensible
    /// third option — cascading would delete other members' boulders, attempts and history along with
    /// the wall, and handing the wall to an arbitrary ordinary member would grant wall-admin authority
    /// to somebody who was never given it. Both alternatives change other people's data without their
    /// say; making the departing owner hand the wall over (or delete it) first does not.
    /// <para>
    /// Called twice per deletion: once provisionally, and once inside the transaction where the
    /// answer is actually used. Only the second answer decides anything.
    /// </para>
    /// </remarks>
    private static async Task<(List<AccountDeletionWallTransfer> Transfers, List<string> Blocking)> ResolveWallOwnershipAsync(
        BlocwerkDbContext db,
        Guid userId,
        CancellationToken ct)
    {
        var ownedWalls = await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.OwnerId == userId)
            .Select(w => new { w.Id, w.Name })
            .ToListAsync(ct);

        var transfers = new List<AccountDeletionWallTransfer>();
        var blocking = new List<string>();

        foreach (var wall in ownedWalls)
        {
            var candidates = await db.WallMembers
                .AsNoTracking()
                .Where(m => m.WallId == wall.Id && m.UserId != userId && m.Role == WallRole.Admin)
                .Where(m => m.User.DeletedAt == null)
                .OrderBy(m => m.JoinedAt)
                .Select(m => new
                {
                    m.UserId,
                    m.User.DisplayName,
                    m.User.CustomDisplayName,
                })
                .ToListAsync(ct);

            var successor = candidates.FirstOrDefault();
            if (successor is null)
            {
                blocking.Add(wall.Name);
                continue;
            }

            transfers.Add(new AccountDeletionWallTransfer
            {
                WallId = wall.Id,
                WallName = wall.Name,
                NewOwnerId = successor.UserId,
                NewOwnerName = string.IsNullOrWhiteSpace(successor.CustomDisplayName)
                    ? successor.DisplayName
                    : successor.CustomDisplayName,
            });
        }

        return (transfers, blocking);
    }

    private static async Task TransferWallOwnershipAsync(
        BlocwerkDbContext db,
        Guid userId,
        IReadOnlyList<AccountDeletionWallTransfer> transfers,
        CancellationToken ct)
    {
        foreach (var transfer in transfers)
        {
            await db.Walls
                .IgnoreQueryFilters()
                .Where(w => w.Id == transfer.WallId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.OwnerId, transfer.NewOwnerId), ct);
        }

        // A maintenance lock held by somebody who is leaving would hide the wall from every remaining
        // member with nobody flagged as the one updating it, so it is released.
        await db.Walls
            .IgnoreQueryFilters()
            .Where(w => w.MaintenanceByUserId == userId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(w => w.UnderMaintenance, false)
                    .SetProperty(w => w.MaintenanceByUserId, (Guid?)null),
                ct);
    }

    /// <summary>
    /// The invariant this whole file exists for, asserted on the written rows before the commit: no
    /// wall may be left owned by a tombstone.
    /// </summary>
    /// <remarks>
    /// A wall owned by somebody who no longer exists has no one who can add admins, reset it or hand
    /// it on, and there is no admin screen to repair that. The sole-owner refusal is supposed to make
    /// it impossible; this is the belt to that braces, reading back what was actually written rather
    /// than trusting the decision that produced it.
    /// </remarks>
    private static async Task AssertNoWallLeftToADeletedOwnerAsync(
        BlocwerkDbContext db,
        IReadOnlyList<AccountDeletionWallTransfer> transfers,
        CancellationToken ct)
    {
        if (transfers.Count == 0)
        {
            return;
        }

        var wallIds = transfers.Select(t => t.WallId).ToList();
        var written = await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => wallIds.Contains(w.Id))
            .Select(w => new { w.Name, w.OwnerId })
            .ToListAsync(ct);

        var ownerIds = written.Select(w => w.OwnerId).Distinct().ToList();
        var liveOwners = await db.Users
            .AsNoTracking()
            .Where(u => ownerIds.Contains(u.Id) && u.DeletedAt == null)
            .Select(u => u.Id)
            .ToListAsync(ct);

        var orphaned = written
            .Where(w => !liveOwners.Contains(w.OwnerId))
            .Select(w => w.Name)
            .ToList();

        if (orphaned.Count > 0)
        {
            throw new AccountDeletionBlockedException(orphaned);
        }
    }
}
