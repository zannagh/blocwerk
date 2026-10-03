// <copyright file="BlocwerkDbContext.Relocation.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration for the wall-update "this hold moved" suggestions.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallUpdateRelocationProposal> WallUpdateRelocationProposals => Set<WallUpdateRelocationProposal>();

    /// <summary>
    /// Same shape as the other decision child tables of <see cref="WallUpdateSession"/> (see
    /// <c>ConfigureWallUpdateSession</c>): CASCADE from the session and from both hold ends, because a
    /// suggestion about a hold that no longer exists must clean itself up, and <c>WithMany()</c> with no
    /// principal navigation so <see cref="ChangeJournalCascadeGuard"/> never treats it as a blocker.
    /// </summary>
    private static void ConfigureRelocationProposals(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallUpdateRelocationProposal>(entity =>
        {
            entity.HasOne<WallUpdateSession>()
                .WithMany()
                .HasForeignKey(p => p.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.OldHold)
                .WithMany()
                .HasForeignKey(p => p.OldHoldId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.NewHold)
                .WithMany()
                .HasForeignKey(p => p.NewHoldId)
                .OnDelete(DeleteBehavior.Cascade);

            // The matcher is 1:1, so each hold appears at most once per session on either side. The
            // store enforces it: two concurrent resumes computing the list cannot both insert it.
            entity.HasIndex(p => new { p.SessionId, p.OldHoldId }).IsUnique();
            entity.HasIndex(p => new { p.SessionId, p.NewHoldId }).IsUnique();
        });
    }
}
