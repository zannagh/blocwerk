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

    public DbSet<WallSegment> WallSegments => Set<WallSegment>();

    public DbSet<Hold> Holds => Set<Hold>();

    public DbSet<Boulder> Boulders => Set<Boulder>();

    public DbSet<BoulderHold> BoulderHolds => Set<BoulderHold>();

    public DbSet<Attempt> Attempts => Set<Attempt>();

    public DbSet<WallReset> WallResets => Set<WallReset>();

    public DbSet<ActivityLogEntry> ActivityLog => Set<ActivityLogEntry>();

    public DbSet<BoulderComment> BoulderComments => Set<BoulderComment>();

    public DbSet<BetaVideo> BetaVideos => Set<BetaVideo>();

    public DbSet<GradeProposal> GradeProposals => Set<GradeProposal>();

    public DbSet<BoulderRating> BoulderRatings => Set<BoulderRating>();

    public DbSet<BoulderFavorite> BoulderFavorites => Set<BoulderFavorite>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<HangboardSession> HangboardSessions => Set<HangboardSession>();

    public DbSet<PullupSession> PullupSessions => Set<PullupSession>();

    public DbSet<ClimbingSession> ClimbingSessions => Set<ClimbingSession>();

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<WallTemperatureReading> WallTemperatureReadings => Set<WallTemperatureReading>();

    public DbSet<WallImage> WallImages => Set<WallImage>();

    public DbSet<WallStitchJob> WallStitchJobs => Set<WallStitchJob>();

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
        ConfigureWallSegment(modelBuilder);
        ConfigureHold(modelBuilder);
        ConfigureBoulder(modelBuilder);
        ConfigureBoulderHold(modelBuilder);
        ConfigureAttempt(modelBuilder);
        ConfigureWallReset(modelBuilder);
        ConfigureActivityLog(modelBuilder);
        ConfigureBoulderComment(modelBuilder);
        ConfigureBetaVideo(modelBuilder);
        ConfigureGradeProposal(modelBuilder);
        ConfigureBoulderRating(modelBuilder);
        ConfigureBoulderFavorite(modelBuilder);
        ConfigureActivity(modelBuilder);
        ConfigureApiKey(modelBuilder);
        ConfigureWallTemperatureReading(modelBuilder);
        ConfigureWallImage(modelBuilder);
        ConfigureWallStitchJob(modelBuilder);
    }

    private static void ConfigureWallStitchJob(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallStitchJob>(entity =>
        {
            // A deleted wall takes its stitch history with it; the jobs address nothing else.
            entity.HasOne(j => j.Wall)
                .WithMany()
                .HasForeignKey(j => j.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            // The listing is always "this wall, newest run first".
            entity.HasIndex(j => new { j.WallId, j.CreatedAt });
        });
    }

    private static void ConfigureApiKey(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKey>(entity =>
        {
            // Validation is a hash lookup on every machine request, so it has to be an index hit.
            entity.HasIndex(k => k.KeyHash).IsUnique();

            entity.HasOne(k => k.User)
                .WithMany()
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // A deleted wall takes its wall-scoped keys with it; they can address nothing else.
            entity.HasOne(k => k.Wall)
                .WithMany()
                .HasForeignKey(k => k.WallId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureWallTemperatureReading(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallTemperatureReading>(entity =>
        {
            entity.HasOne(r => r.Wall)
                .WithMany()
                .HasForeignKey(r => r.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            // Every read is "this wall, this time range".
            entity.HasIndex(r => new { r.WallId, r.RecordedAt });
        });
    }

    private static void ConfigureWallImage(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallImage>(entity =>
        {
            entity.HasOne(i => i.Wall)
                .WithMany()
                .HasForeignKey(i => i.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            // The gallery query is always "this wall, newest capture first".
            entity.HasIndex(i => new { i.WallId, i.CapturedAt });
        });
    }

    private static void ConfigureActivity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Activity>(entity =>
        {
            entity.HasIndex(a => new { a.UserId, a.StartedAt });

            entity.HasOne(a => a.User)
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // A wall can be deleted while its activities remain (training-only after that).
            entity.HasOne(a => a.Wall)
                .WithMany()
                .HasForeignKey(a => a.WallId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // The event → activity links are all optional; deleting an activity detaches its events
        // (SetNull) rather than deleting the logged history. Indexed for the per-activity lookups.
        modelBuilder.Entity<Attempt>(entity =>
        {
            entity.HasOne(a => a.Activity)
                .WithMany()
                .HasForeignKey(a => a.ActivityId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(a => a.ActivityId);
        });

        modelBuilder.Entity<HangboardSession>(entity =>
        {
            entity.HasOne(h => h.Activity)
                .WithMany()
                .HasForeignKey(h => h.ActivityId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(h => h.ActivityId);
        });

        modelBuilder.Entity<PullupSession>(entity =>
        {
            entity.HasOne(p => p.Activity)
                .WithMany()
                .HasForeignKey(p => p.ActivityId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(p => p.ActivityId);
        });
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

    private static void ConfigureWallSegment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallSegment>(entity =>
        {
            entity.HasOne(s => s.Wall)
                .WithMany(w => w.Segments)
                .HasForeignKey(s => s.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => new { s.WallId, s.SortOrder });

            entity.Property(s => s.Points)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<ShapePoint>>(v, (JsonSerializerOptions?)null) ?? new List<ShapePoint>());
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

            entity.HasIndex(h => new { h.WallId, h.Generation });

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

            // Offline replay: the same queued attempt may arrive twice.
            entity.HasIndex(a => a.ClientRequestId)
                .IsUnique()
                .HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
    }

    private static void ConfigureWallReset(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallReset>(entity =>
        {
            entity.HasIndex(wr => new { wr.WallId, wr.Generation }).IsUnique();

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

    private static void ConfigureActivityLog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActivityLogEntry>(entity =>
        {
            entity.HasOne(a => a.Wall)
                .WithMany()
                .HasForeignKey(a => a.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Boulder)
                .WithMany()
                .HasForeignKey(a => a.BoulderId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(a => a.User)
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(a => new { a.WallId, a.Timestamp });
            entity.HasIndex(a => new { a.BoulderId, a.Timestamp });
        });
    }

    private static void ConfigureGradeProposal(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GradeProposal>(entity =>
        {
            entity.HasOne(gp => gp.Boulder)
                .WithMany(b => b.GradeProposals)
                .HasForeignKey(gp => gp.BoulderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(gp => gp.ProposedBy)
                .WithMany()
                .HasForeignKey(gp => gp.ProposedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureBoulderRating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BoulderRating>(entity =>
        {
            entity.HasKey(r => new { r.BoulderId, r.UserId });

            entity.HasOne(r => r.Boulder)
                .WithMany(b => b.Ratings)
                .HasForeignKey(r => r.BoulderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureBoulderFavorite(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BoulderFavorite>(entity =>
        {
            entity.HasKey(f => new { f.BoulderId, f.UserId });

            entity.HasOne(f => f.Boulder)
                .WithMany(b => b.Favorites)
                .HasForeignKey(f => f.BoulderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(f => f.User)
                .WithMany()
                .HasForeignKey(f => f.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureBoulderComment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BoulderComment>(entity =>
        {
            entity.HasOne(c => c.Boulder)
                .WithMany(b => b.Comments)
                .HasForeignKey(c => c.BoulderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Offline replay: a comment has no natural dedupe key.
            entity.HasIndex(c => c.ClientRequestId)
                .IsUnique()
                .HasFilter("\"ClientRequestId\" IS NOT NULL");
        });
    }

    private static void ConfigureBetaVideo(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BetaVideo>(entity =>
        {
            entity.HasOne(v => v.Boulder)
                .WithMany(b => b.BetaVideos)
                .HasForeignKey(v => v.BoulderId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a user must not silently take their beta with it — same stance as comments.
            entity.HasOne(v => v.UploadedBy)
                .WithMany()
                .HasForeignKey(v => v.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // The carousel query is always "this boulder, newest first".
            entity.HasIndex(v => new { v.BoulderId, v.CreatedAt });
        });
    }
}
