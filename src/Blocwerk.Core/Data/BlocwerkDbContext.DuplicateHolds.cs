using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration for the dismissed duplicate-hold suggestions (<see cref="HoldDuplicateDismissal"/>).</summary>
public partial class BlocwerkDbContext
{
    public DbSet<HoldDuplicateDismissal> HoldDuplicateDismissals => Set<HoldDuplicateDismissal>();

    /// <remarks>A satellite table off its wall, with no principal-side navigation (the repo convention).</remarks>
    private static void ConfigureHoldDuplicates(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HoldDuplicateDismissal>(entity =>
        {
            entity.HasOne(d => d.Wall)
                .WithMany()
                .HasForeignKey(d => d.WallId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(d => new { d.WallId, d.HoldAId, d.HoldBId }).IsUnique();
        });
    }
}
