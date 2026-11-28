// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the companion app's write receipts (<see cref="DeviceWriteReceipt"/>). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<DeviceWriteReceipt> DeviceWriteReceipts => Set<DeviceWriteReceipt>();

    /// <remarks>
    /// Satellite table hanging off its user with <c>WithMany()</c>, gone with the user. The replay key is unique per user,
    /// which is what lets exactly one of two concurrent retries take it.
    /// </remarks>
    private static void ConfigureDeviceWrites(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceWriteReceipt>(entity =>
        {
            entity.HasIndex(r => new { r.UserId, r.ClientRequestId }).IsUnique();

            entity.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
