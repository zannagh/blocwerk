// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// Model configuration of the 3D runners (GPU machines that pull splat training) and their job
/// queue. Strictly additive, in its own partial like the capture tables.
/// </summary>
public partial class BlocwerkDbContext
{
    public DbSet<GpuRunner> GpuRunners => Set<GpuRunner>();

    public DbSet<GpuRunnerWall> GpuRunnerWalls => Set<GpuRunnerWall>();

    public DbSet<GpuJob> GpuJobs => Set<GpuJob>();

    public DbSet<GpuRunnerSharedOptIn> GpuRunnerSharedOptIns => Set<GpuRunnerSharedOptIn>();

    /// <remarks>
    /// Satellites hang off their principals with <c>WithMany()</c> (repo convention). Deleting a
    /// wall cascades its runner assignments and jobs; deleting a runner cascades its assignments and
    /// only detaches the jobs it held (they go back to the queue via the lease sweep).
    /// </remarks>
    private static void ConfigureGpuRunners(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GpuRunner>(entity =>
        {
            entity.HasOne(r => r.Owner).WithMany().HasForeignKey(r => r.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(r => r.KeyHash).IsUnique();
            entity.HasIndex(r => r.OwnerUserId);
        });

        modelBuilder.Entity<GpuRunnerWall>(entity =>
        {
            entity.HasKey(rw => new { rw.RunnerId, rw.WallId });
            entity.HasOne(rw => rw.Runner).WithMany().HasForeignKey(rw => rw.RunnerId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(rw => rw.Wall).WithMany().HasForeignKey(rw => rw.WallId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(rw => rw.WallId);
        });

        modelBuilder.Entity<GpuJob>(entity =>
        {
            entity.HasOne(j => j.Wall).WithMany().HasForeignKey(j => j.WallId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(j => j.Capture).WithMany().HasForeignKey(j => j.CaptureId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(j => j.ClaimedByRunner).WithMany().HasForeignKey(j => j.ClaimedByRunnerId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(j => j.ResultStatsJson).HasColumnType("text");
            entity.HasIndex(j => new { j.Status, j.CreatedAt });
            entity.HasIndex(j => j.CaptureId);
        });

        modelBuilder.Entity<GpuRunnerSharedOptIn>(entity =>
        {
            entity.HasOne(o => o.Wall).WithMany().HasForeignKey(o => o.WallId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
