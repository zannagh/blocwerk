// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// The replay keys of the training and session writes a phone's offline queue may send twice: unique per user, like the
/// attempts' and comments' keys, so an id someone else sent never collides with or answers for one's own. The plain
/// user index stays beside each: the replay index is partial, so it cannot serve a lookup by user.
/// </summary>
public partial class BlocwerkDbContext
{
    private static void ConfigureReplayKeys(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HangboardSession>(entity =>
        {
            entity.HasIndex(h => h.UserId);
            entity.HasIndex(h => new { h.UserId, h.ClientRequestId })
                .IsUnique()
                .HasFilter("\"ClientRequestId\" IS NOT NULL");
        });

        modelBuilder.Entity<PullupSession>(entity =>
        {
            entity.HasIndex(p => p.UserId);
            entity.HasIndex(p => new { p.UserId, p.ClientRequestId })
                .IsUnique()
                .HasFilter("\"ClientRequestId\" IS NOT NULL");
        });

        modelBuilder.Entity<ClimbingSession>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => new { s.UserId, s.ClientRequestId })
                .IsUnique()
                .HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
    }
}
