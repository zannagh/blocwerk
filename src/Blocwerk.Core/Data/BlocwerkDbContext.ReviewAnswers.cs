// <copyright file="BlocwerkDbContext.ReviewAnswers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration for the answers of the one review of a wall update.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallUpdateReviewAnswer> WallUpdateReviewAnswers => Set<WallUpdateReviewAnswer>();

    /// <summary>
    /// Same shape as the session's other decision rows: CASCADE from the session and from the hold it is about, and
    /// <c>WithMany()</c> with no principal navigation so <see cref="ChangeJournalCascadeGuard"/> never treats it as a
    /// blocker. One answer per hold and subject per session: the upsert key of the answer path.
    /// </summary>
    private static void ConfigureReviewAnswers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallUpdateReviewAnswer>(entity =>
        {
            entity.HasOne<WallUpdateSession>()
                .WithMany()
                .HasForeignKey(a => a.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Hold>()
                .WithMany()
                .HasForeignKey(a => a.HoldId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(a => new { a.SessionId, a.HoldId, a.Subject }).IsUnique();
        });
    }
}
