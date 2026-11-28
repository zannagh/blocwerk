// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the one-time device link codes (<see cref="DeviceLinkCode"/>). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<DeviceLinkCode> DeviceLinkCodes => Set<DeviceLinkCode>();

    /// <remarks>
    /// Satellite table hanging off its user with <c>WithMany()</c>, gone with the user. The code hash is the lookup of
    /// every exchange, so it is a unique index; the expiry index serves the clean-up of old rows.
    /// </remarks>
    private static void ConfigureDeviceLinks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceLinkCode>(entity =>
        {
            entity.HasIndex(c => c.CodeHash).IsUnique();
            entity.HasIndex(c => c.ExpiresAt);

            entity.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
