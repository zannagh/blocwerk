// <copyright file="BlocwerkDbContext.ShapeReview.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration for the wall-update shape recognition proposals.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallUpdateShapeProposal> WallUpdateShapeProposals => Set<WallUpdateShapeProposal>();

    /// <summary>
    /// Same shape as the other decision child tables of <see cref="WallUpdateSession"/>: CASCADE from the
    /// session and from the staged hold, <c>WithMany()</c> with no principal navigation so
    /// <see cref="ChangeJournalCascadeGuard"/> never treats a proposal as a blocker.
    /// </summary>
    private static void ConfigureShapeProposals(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallUpdateShapeProposal>(entity =>
        {
            entity.HasOne<WallUpdateSession>()
                .WithMany()
                .HasForeignKey(p => p.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Hold)
                .WithMany()
                .HasForeignKey(p => p.HoldId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(p => p.ShapeJson).HasColumnType("text");
            entity.Property(p => p.HolesJson).HasColumnType("text");
            entity.Property(p => p.PreviousShapeJson).HasColumnType("text");
            entity.Property(p => p.AdjustedShapeJson).HasColumnType("text");

            // One proposal per staged hold per session: a resumed run skips holds that already have one.
            entity.HasIndex(p => new { p.SessionId, p.HoldId }).IsUnique();
        });
    }
}
