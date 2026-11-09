// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the recognition job queue (runner protocol 1.2). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<RecognitionJob> RecognitionJobs => Set<RecognitionJob>();

    /// <remarks>
    /// Deleting a wall cascades its jobs; deleting a runner only detaches the jobs it held (the lease sweep requeues them).
    /// The pick reads queued jobs by lane, then age.
    /// </remarks>
    private static void ConfigureRecognitionJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecognitionJob>(entity =>
        {
            entity.HasOne(j => j.Wall).WithMany().HasForeignKey(j => j.WallId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(j => j.ClaimedByRunner).WithMany().HasForeignKey(j => j.ClaimedByRunnerId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(j => j.ResultJson).HasColumnType("text");
            entity.HasIndex(j => new { j.Status, j.Lane, j.CreatedAt });
            entity.HasIndex(j => j.WallId);
            entity.HasIndex(j => j.CallerKey);
        });
    }
}
