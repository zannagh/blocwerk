// <copyright file="BlocwerkDbContext.Volumes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// Model configuration of the marker-free volumes (<see cref="WallVolume"/>). Strictly additive and in its own
/// partial, so its migration can be regenerated independently at merge time.
/// </summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallVolume> WallVolumes => Set<WallVolume>();

    /// <remarks>Hangs off the model with <c>WithMany()</c> (repo convention); deleting the model or its wall cascades.</remarks>
    private static void ConfigureWallVolumes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallVolume>(entity =>
        {
            entity.HasOne(v => v.GeometryModel)
                .WithMany()
                .HasForeignKey(v => v.GeometryModelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(v => v.FootprintJson).HasColumnType("text");
            entity.Property(v => v.SurfaceJson).HasColumnType("text");
            entity.Property(v => v.HeightFieldJson).HasColumnType("text");
            entity.HasIndex(v => new { v.GeometryModelId, v.Index });
            entity.HasIndex(v => v.WallId);
        });
    }
}
