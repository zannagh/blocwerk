// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// Model configuration of the photo-real (Gaussian splat) scene of a geometry model. Strictly
/// additive and in its own partial, like the capture tables.
/// </summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallGeometrySplat> WallGeometrySplats => Set<WallGeometrySplat>();

    /// <remarks>
    /// Hangs off the model with <c>WithMany()</c> (repo convention, no principal-side navigation);
    /// deleting a model or its wall cascades the row, the file on disk is orphaned and harmless.
    /// </remarks>
    private static void ConfigureWallGeometrySplat(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallGeometrySplat>(entity =>
        {
            entity.HasOne(s => s.GeometryModel)
                .WithMany()
                .HasForeignKey(s => s.GeometryModelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(s => s.FrameJson).HasColumnType("text");
            entity.HasIndex(s => s.GeometryModelId).IsUnique();
        });
    }
}
