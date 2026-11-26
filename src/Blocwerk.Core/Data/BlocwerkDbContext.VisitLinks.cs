// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the one-time visitor links (<see cref="WallVisitLink"/>). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallVisitLink> WallVisitLinks => Set<WallVisitLink>();

    /// <remarks>
    /// Satellite table: hangs off its wall and its creator with <c>WithMany()</c> and no principal-side navigation, and
    /// goes with either. The token hash is the lookup on every open, so it is a unique index; the creator and time
    /// index serves the per-member hourly cap, and the expiry index the clean-up of old rows.
    /// </remarks>
    private static void ConfigureVisitLinks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallVisitLink>(entity =>
        {
            entity.HasIndex(l => l.TokenHash).IsUnique();
            entity.HasIndex(l => new { l.CreatedByUserId, l.CreatedAt });
            entity.HasIndex(l => l.ExpiresAt);

            entity.HasOne(l => l.Wall)
                .WithMany()
                .HasForeignKey(l => l.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(l => l.CreatedBy)
                .WithMany()
                .HasForeignKey(l => l.CreatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
