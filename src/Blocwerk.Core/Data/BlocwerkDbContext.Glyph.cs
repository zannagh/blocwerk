using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// Model configuration for the experimental glyph (ArUco marker) wall geometry: solved geometry
/// models per wall and raw marker observations per panel photo. Strictly additive — nothing here
/// touches the normalized per-panel hold columns.
/// </summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallGeometryModel> WallGeometryModels => Set<WallGeometryModel>();

    public DbSet<WallMarkerObservation> WallMarkerObservations => Set<WallMarkerObservation>();

    /// <remarks>
    /// Both tables hang off their principal with <c>WithMany()</c> and no principal-side navigation,
    /// the repo convention for satellite tables — which also keeps <see cref="ChangeJournalCascadeGuard"/>
    /// (it enumerates cascade dependents via collection navigations) from treating them as a reason to
    /// block a journal revert of a wall or panel.
    /// </remarks>
    private static void ConfigureGlyphGeometry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallGeometryModel>(entity =>
        {
            entity.HasOne(m => m.Wall)
                .WithMany()
                .HasForeignKey(m => m.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(m => m.Json).HasColumnType("text");

            // Newest-first history per wall.
            entity.HasIndex(m => new { m.WallId, m.CreatedAt });

            // "At most one ACTIVE model per wall" is an invariant, so the store enforces it with a
            // partial unique index (Postgres and SQLite both support filtered indexes). Inactive rows
            // are history and stay unconstrained. Activating a model must deactivate the previous one
            // in the same SaveChanges, or the insert/update fails here.
            entity.HasIndex(m => m.WallId)
                .HasDatabaseName("IX_WallGeometryModels_WallId_Active")
                .HasFilter("\"IsActive\" = TRUE")
                .IsUnique();
        });

        modelBuilder.Entity<WallMarkerObservation>(entity =>
        {
            entity.HasOne(o => o.WallPanel)
                .WithMany()
                .HasForeignKey(o => o.WallPanelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(o => o.CornersJson).HasColumnType("text");

            entity.HasIndex(o => new { o.WallPanelId, o.PanelGeneration });
        });
    }
}
