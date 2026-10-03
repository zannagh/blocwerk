// <copyright file="BlocwerkDbContext.PanelCrop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the kept originals of cropped panel photos (<see cref="WallPanelCrop"/>). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallPanelCrop> WallPanelCrops => Set<WallPanelCrop>();

    /// <remarks>
    /// Hangs off its panel with <c>WithOne()</c> and no principal-side navigation (the satellite-table convention, which
    /// also keeps <see cref="ChangeJournalCascadeGuard"/> from treating it as a reason to block a revert); deleting the
    /// panel cascades.
    /// </remarks>
    private static void ConfigurePanelCrops(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallPanelCrop>(entity =>
        {
            entity.HasOne(c => c.WallPanel)
                .WithOne()
                .HasForeignKey<WallPanelCrop>(c => c.WallPanelId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
