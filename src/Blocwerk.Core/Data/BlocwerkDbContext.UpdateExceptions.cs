// <copyright file="BlocwerkDbContext.UpdateExceptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration for the cards of the "Update panels + 3D" confirm screen.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallUpdateException> WallUpdateExceptions => Set<WallUpdateException>();

    /// <summary>
    /// Same shape as <see cref="WallUpdateRelocationProposal"/>: CASCADE from the session and from both (optional) hold
    /// ends, and <c>WithMany()</c> with no principal navigation so <see cref="ChangeJournalCascadeGuard"/> never treats
    /// it as a blocker.
    /// </summary>
    private static void ConfigureUpdateExceptions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallUpdateException>(entity =>
        {
            entity.HasOne<WallUpdateSession>()
                .WithMany()
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Hold>()
                .WithMany()
                .HasForeignKey(e => e.OldHoldId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Hold>()
                .WithMany()
                .HasForeignKey(e => e.StagedHoldId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.SessionId, e.Kind });
        });
    }
}
