// <copyright file="BlocwerkDbContext.HoldProposals.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the multi-view hold proposals (<see cref="HoldProposal"/>). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<HoldProposal> HoldProposals => Set<HoldProposal>();

    /// <remarks>Hangs off its wall with <c>WithMany()</c> (repo convention); deleting the wall cascades.</remarks>
    private static void ConfigureHoldProposals(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HoldProposal>(entity =>
        {
            entity.HasOne(p => p.Wall)
                .WithMany()
                .HasForeignKey(p => p.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(p => new { p.WallId, p.Status });
        });
    }
}
