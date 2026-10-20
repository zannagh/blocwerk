// <copyright file="BlocwerkDbContext.Enhancement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the one-time wall enhancement (runs and their review items). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallEnhancementRun> WallEnhancementRuns => Set<WallEnhancementRun>();

    public DbSet<WallEnhancementItem> WallEnhancementItems => Set<WallEnhancementItem>();

    public DbSet<CaptureDetectionRecord> CaptureDetectionRecords => Set<CaptureDetectionRecord>();

    private static void ConfigureWallEnhancement(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallEnhancementRun>(entity =>
        {
            entity.HasOne(r => r.Wall)
                .WithMany()
                .HasForeignKey(r => r.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(r => r.PanelGenerationsJson).HasColumnType("text");
            entity.Property(r => r.StepsJson).HasColumnType("text");
            entity.Property(r => r.OutcomeJson).HasColumnType("text");

            // The rule itself: one enhancement per wall generation, whatever the code above it does.
            entity.HasIndex(r => new { r.WallId, r.Generation }).IsUnique();
        });

        modelBuilder.Entity<WallEnhancementItem>(entity =>
        {
            entity.HasOne(i => i.Run)
                .WithMany()
                .HasForeignKey(i => i.RunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(i => new { i.RunId, i.Kind, i.Decision });
        });

        modelBuilder.Entity<CaptureDetectionRecord>(entity =>
        {
            entity.HasKey(r => new { r.StoredPath, r.Detector });
            entity.Property(r => r.DetectionsJson).HasColumnType("text");
        });
    }
}
