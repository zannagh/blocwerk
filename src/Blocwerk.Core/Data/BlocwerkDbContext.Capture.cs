using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// Model configuration for the in-app glyph capture flow: captures, their photos (bytes on disk) and
/// the per-facet textures of a solved geometry model. Strictly additive, kept in its own partial so
/// the migration can be regenerated independently at merge time.
/// </summary>
public partial class BlocwerkDbContext
{
    public DbSet<WallCapture> WallCaptures => Set<WallCapture>();

    public DbSet<WallCapturePhoto> WallCapturePhotos => Set<WallCapturePhoto>();

    public DbSet<WallGeometryTexture> WallGeometryTextures => Set<WallGeometryTexture>();

    /// <remarks>
    /// Satellite tables hang off their principal with <c>WithMany()</c> and no principal-side
    /// navigation (the repo convention, which also keeps <see cref="ChangeJournalCascadeGuard"/> out
    /// of it). Deleting a wall cascades its captures, photos and textures in the database; the files
    /// on disk are orphaned then, and <c>WallCaptureSweeper</c> deletes them (wall photos can show people).
    /// </remarks>
    private static void ConfigureWallCapture(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallCapture>(entity =>
        {
            entity.HasOne(c => c.Wall)
                .WithMany()
                .HasForeignKey(c => c.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(c => c.DeclarationsJson).HasColumnType("text");
            entity.Property(c => c.PlanJson).HasColumnType("text");
            entity.Property(c => c.PlacementCheckJson).HasColumnType("text");
            entity.HasIndex(c => new { c.WallId, c.CreatedAt });
            entity.HasIndex(c => c.Status);
        });

        modelBuilder.Entity<WallCapturePhoto>(entity =>
        {
            entity.HasOne(p => p.Capture)
                .WithMany()
                .HasForeignKey(p => p.CaptureId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(p => p.MarkersJson).HasColumnType("text");
            entity.HasIndex(p => new { p.CaptureId, p.Index }).IsUnique();
        });

        modelBuilder.Entity<WallGeometryTexture>(entity =>
        {
            entity.HasOne(t => t.GeometryModel)
                .WithMany()
                .HasForeignKey(t => t.GeometryModelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(t => new { t.GeometryModelId, t.FacetId }).IsUnique();
        });
    }
}
