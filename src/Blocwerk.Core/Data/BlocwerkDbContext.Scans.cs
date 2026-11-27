// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of the iPhone scan packages (<see cref="WallScan"/>). Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallScan> WallScans => Set<WallScan>();

    /// <remarks>
    /// Satellite of its capture (<c>WithOne()</c>, no principal-side navigation): deleting the capture, or its wall,
    /// deletes the row; the files are then left to the scan retention, which removes a folder whose capture is gone.
    /// </remarks>
    private static void ConfigureWallScans(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallScan>(entity =>
        {
            entity.HasOne(s => s.Capture)
                .WithOne()
                .HasForeignKey<WallScan>(s => s.CaptureId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(s => s.ManifestJson).HasColumnType("text");
        });
    }
}
