using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// Model configuration for the texture-registration placement run log (<see cref="HoldPlacementRun"/>).
/// Strictly additive, kept in its own partial so the migration can be regenerated independently at merge time.
/// </summary>
public partial class BlocwerkDbContext
{
    public DbSet<HoldPlacementRun> HoldPlacementRuns => Set<HoldPlacementRun>();

    /// <remarks>
    /// A satellite table hanging off its wall with <c>WithMany()</c> and no principal-side navigation (the
    /// repo convention, which keeps <see cref="ChangeJournalCascadeGuard"/> out of it).
    /// </remarks>
    private static void ConfigureHoldPlacement(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HoldPlacementRun>(entity =>
        {
            entity.HasOne(r => r.Wall)
                .WithMany()
                .HasForeignKey(r => r.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(r => r.HoldsJson).HasColumnType("text");
            entity.Property(r => r.PanelsJson).HasColumnType("text");

            // Newest-first per wall ("revert the latest run").
            entity.HasIndex(r => new { r.WallId, r.CreatedAt });
        });
    }
}
