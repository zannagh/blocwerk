// <copyright file="BlocwerkDbContext.MarkerPlan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration for saved marker plans (the marker planner's history per wall).</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallMarkerPlan> WallMarkerPlans => Set<WallMarkerPlan>();

    /// <remarks>
    /// Same shape as the geometry models: <c>WithMany()</c> without a principal-side navigation (the
    /// satellite-table convention), cascade on wall delete, and a filtered unique index for "one
    /// current plan per wall" — so swapping the current plan must retire the old row in its own
    /// SaveChanges first (see <c>MarkerPlanService</c>).
    /// </remarks>
    private static void ConfigureMarkerPlan(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallMarkerPlan>(entity =>
        {
            entity.HasOne(p => p.Wall)
                .WithMany()
                .HasForeignKey(p => p.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(p => p.Json).HasColumnType("text");

            entity.HasIndex(p => new { p.WallId, p.CreatedAt });

            entity.HasIndex(p => p.WallId)
                .HasDatabaseName("IX_WallMarkerPlans_WallId_Current")
                .HasFilter("\"IsCurrent\" = TRUE")
                .IsUnique();
        });
    }
}
