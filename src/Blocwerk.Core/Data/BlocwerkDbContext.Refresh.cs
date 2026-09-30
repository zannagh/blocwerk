// <copyright file="BlocwerkDbContext.Refresh.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the "Update panels + 3D" runs. Strictly additive, in its own partial.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallRefresh> WallRefreshes => Set<WallRefresh>();

    private static void ConfigureWallRefresh(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallRefresh>(entity =>
        {
            entity.HasOne(r => r.Wall)
                .WithMany()
                .HasForeignKey(r => r.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(r => r.VideosJson).HasColumnType("text");
            entity.Property(r => r.PanelPicksJson).HasColumnType("text");
            entity.Property(r => r.StepsJson).HasColumnType("text");
            entity.Property(r => r.SummaryJson).HasColumnType("text");

            // One open run per wall (Uploading .. Applying); finished, failed and discarded runs stay as history.
            entity.HasIndex(r => r.WallId)
                .IsUnique()
                .HasFilter("\"Status\" IN (0, 1, 2, 3, 4, 5)")
                .HasDatabaseName("IX_WallRefreshes_WallId_Open");
        });
    }
}
