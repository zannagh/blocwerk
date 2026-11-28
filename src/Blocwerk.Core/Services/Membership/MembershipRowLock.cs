// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services.Membership;

/// <summary>
/// Locks the member rows an ending touches (the person leaving or removed, and the removing admin) until the transaction
/// ends, so the guards read after it see what a concurrent ending committed.
/// </summary>
/// <remarks>
/// Two admins removing each other at the same moment both passed an unlocked admin check, and each then deleted the
/// other: the wall lost both. Under the lock the second waits, then finds its own admin row gone and is refused. The rows
/// are locked in user-id order, so two endings over the same pair cannot deadlock. SQLite (the tests) has one writer at
/// a time already and no <c>FOR UPDATE</c>.
/// </remarks>
internal static class MembershipRowLock
{
    public static async Task LockAsync(BlocwerkDbContext db, Guid wallId, Guid[] userIds, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"WallMembers\" WHERE \"WallId\" = {0} AND \"UserId\" = ANY({1}) ORDER BY \"UserId\" FOR UPDATE",
            [wallId, userIds],
            ct);
    }
}
