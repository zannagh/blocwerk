using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

public class BlocwerkDbContext : DbContext
{
    public Guid CurrentUserId { get; set; } = Guid.Empty;

    public DbSet<User> Users => Set<User>();

    public DbSet<Wall> Walls => Set<Wall>();

    public DbSet<WallMember> WallMembers => Set<WallMember>();

    public DbSet<Hold> Holds => Set<Hold>();

    public DbSet<Boulder> Boulders => Set<Boulder>();

    public DbSet<BoulderHold> BoulderHolds => Set<BoulderHold>();

    public DbSet<Attempt> Attempts => Set<Attempt>();

    public DbSet<WallReset> WallResets => Set<WallReset>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<HangboardSession> HangboardSessions => Set<HangboardSession>();

    public DbSet<PullupSession> PullupSessions => Set<PullupSession>();

    public BlocwerkDbContext(DbContextOptions<BlocwerkDbContext> options)
        : base(options)
    {
    }

    public async Task SetCurrentUserAsync(ICurrentUserService currentUserService)
    {
        try
        {
            var user = await currentUserService.GetCurrentUserAsync();
            CurrentUserId = user.Id;
        }
        catch
        {
            CurrentUserId = Guid.Empty;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUser(modelBuilder);
        ConfigureWall(modelBuilder);
        ConfigureWallMember(modelBuilder);
        ConfigureHold(modelBuilder);
        ConfigureBoulder(modelBuilder);
        ConfigureBoulderHold(modelBuilder);
        ConfigureAttempt(modelBuilder);
        ConfigureWallReset(modelBuilder);
    }

    private static void ConfigureUser(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Identifier).IsUnique();
        });
    }

    private void ConfigureWall(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Wall>(entity =>
        {
            entity.HasOne(w => w.Owner)
                .WithMany()
                .HasForeignKey(w => w.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(w => w.ShareToken)
                .IsUnique()
                .HasFilter("\"ShareToken\" IS NOT NULL");

            entity.Property(w => w.BorderPoints)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<List<ShapePoint>>(v, (JsonSerializerOptions?)null));

            entity.HasQueryFilter(w =>
                CurrentUserId == Guid.Empty
                || w.Members.Any(m => m.UserId == CurrentUserId));
        });
    }

    private static void ConfigureWallMember(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallMember>(entity =>
        {
            entity.HasKey(wm => new { wm.UserId, wm.WallId });

            entity.HasOne(wm => wm.User)
                .WithMany(u => u.WallMemberships)
                .HasForeignKey(wm => wm.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(wm => wm.Wall)
                .WithMany(w => w.Members)
                .HasForeignKey(wm => wm.WallId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureHold(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Hold>(entity =>
        {
            entity.HasOne(h => h.Wall)
                .WithMany(w => w.Holds)
                .HasForeignKey(h => h.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(h => h.ShapePoints)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<List<ShapePoint>>(v, (JsonSerializerOptions?)null));
        });
    }

    private static void ConfigureBoulder(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Boulder>(entity =>
        {
            entity.HasOne(b => b.Wall)
                .WithMany(w => w.Boulders)
                .HasForeignKey(b => b.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(b => b.CreatedBy)
                .WithMany()
                .HasForeignKey(b => b.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureBoulderHold(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BoulderHold>(entity =>
        {
            entity.HasKey(bh => new { bh.BoulderId, bh.HoldId });

            entity.HasOne(bh => bh.Boulder)
                .WithMany(b => b.BoulderHolds)
                .HasForeignKey(bh => bh.BoulderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(bh => bh.Hold)
                .WithMany(h => h.BoulderHolds)
                .HasForeignKey(bh => bh.HoldId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAttempt(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Attempt>(entity =>
        {
            entity.HasOne(a => a.Boulder)
                .WithMany(b => b.Attempts)
                .HasForeignKey(a => a.BoulderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.User)
                .WithMany(u => u.Attempts)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureWallReset(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallReset>(entity =>
        {
            entity.HasOne(wr => wr.Wall)
                .WithMany(w => w.Resets)
                .HasForeignKey(wr => wr.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(wr => wr.ResetBy)
                .WithMany()
                .HasForeignKey(wr => wr.ResetByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
