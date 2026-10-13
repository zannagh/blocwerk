// <copyright file="BlocwerkDbContext.LookOver.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration for the findings of the wall look-over (<see cref="WallLookOverFinding"/>).</summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallLookOverFinding> WallLookOverFindings => Set<WallLookOverFinding>();

    /// <remarks>A satellite table off its wall, with no principal-side navigation (the repo convention).</remarks>
    private static void ConfigureLookOver(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallLookOverFinding>(entity =>
        {
            entity.HasOne(f => f.Wall)
                .WithMany()
                .HasForeignKey(f => f.WallId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(f => new { f.WallId, f.Status });

            // A discarded detection's outline, in the JSON column convention of Hold.ShapePoints.
            entity.Property(f => f.ShapePoints)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<List<ShapePoint>>(v, (JsonSerializerOptions?)null),
                    NullableShapePointsComparer);
            entity.Property(f => f.ShapeHoles)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<List<List<ShapePoint>>>(v, (JsonSerializerOptions?)null),
                    NullableShapeRingsComparer);
        });
    }
}
