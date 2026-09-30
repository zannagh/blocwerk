// <copyright file="BlocwerkDbContext.HoldLinkSuggestions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the cross-panel link suggestions (<see cref="HoldLinkSuggestion"/>). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<HoldLinkSuggestion> HoldLinkSuggestions => Set<HoldLinkSuggestion>();

    /// <remarks>Hangs off its wall with <c>WithMany()</c> (repo convention); deleting the wall cascades.</remarks>
    private static void ConfigureHoldLinkSuggestions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HoldLinkSuggestion>(entity =>
        {
            entity.HasOne(s => s.Wall)
                .WithMany()
                .HasForeignKey(s => s.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.WallId, s.HoldAId, s.HoldBId }).IsUnique();
        });
    }
}
