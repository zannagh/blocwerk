using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Spins up an isolated SQLite-backed <see cref="BlocwerkDbContext"/> with a seeded
/// wall, so the staging and boulder services can be exercised without Postgres.
/// </summary>
public sealed class WallTestHarness : IDisposable
{
    private readonly SqliteConnection connection;

    public WallTestHarness()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        Owner = new User { Identifier = "owner@test", DisplayName = "Owner" };
        DbContextFactory = new TestDbContextFactory(connection);

        using (var db = DbContextFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        CurrentUser = Substitute.For<ICurrentUserService>();
        CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(Owner));

        ActivityLog = Substitute.For<IActivityLogService>();
        HoldDetection = Substitute.For<IHoldDetectionService>();
        ImageAlignment = Substitute.For<IImageAlignmentService>();

        WallService = new WallService(DbContextFactory, CurrentUser, HoldDetection, ImageAlignment, ActivityLog, NullLogger<WallService>.Instance);
        BoulderService = new BoulderService(DbContextFactory, CurrentUser, ActivityLog, NullLogger<BoulderService>.Instance);
        AttemptService = new AttemptService(DbContextFactory, CurrentUser, ActivityLog, NullLogger<AttemptService>.Instance);
        FeedbackService = new BoulderFeedbackService(DbContextFactory, CurrentUser, NullLogger<BoulderFeedbackService>.Instance);
        SegmentService = new WallSegmentService(DbContextFactory, CurrentUser, NullLogger<WallSegmentService>.Instance);
        SessionService = new Blocwerk.Core.Services.SessionService(DbContextFactory, CurrentUser, NullLogger<Blocwerk.Core.Services.SessionService>.Instance);
    }

    public User Owner { get; }

    public Guid WallId { get; private set; }

    public TestDbContextFactory DbContextFactory { get; }

    public ICurrentUserService CurrentUser { get; }

    public IActivityLogService ActivityLog { get; }

    public IHoldDetectionService HoldDetection { get; }

    public IImageAlignmentService ImageAlignment { get; }

    public IWallService WallService { get; }

    public IBoulderService BoulderService { get; }

    public IAttemptService AttemptService { get; }

    public IBoulderFeedbackService FeedbackService { get; }

    public IWallSegmentService SegmentService { get; }

    public ISessionService SessionService { get; }

    /// <summary>
    /// Seeds a wall owned by <see cref="Owner"/> with a live photo and the given number
    /// of holds at the current generation.
    /// </summary>
    public async Task<List<Hold>> SeedWallAsync(int holdCount = 4, int generation = 0)
    {
        await using var db = DbContextFactory.CreateDbContext();
        db.CurrentUserId = Guid.Empty;

        db.Users.Add(Owner);

        var wall = new Wall
        {
            Name = "Test Wall",
            OwnerId = Owner.Id,
            CurrentGeneration = generation,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = Owner.Id, Role = WallRole.Admin });

        var holds = new List<Hold>();
        for (var i = 0; i < holdCount; i++)
        {
            var hold = new Hold
            {
                WallId = wall.Id,
                X = 0.1 * (i + 1),
                Y = 0.1 * (i + 1),
                Radius = 0.02,
                Generation = generation,
            };
            holds.Add(hold);
            db.Holds.Add(hold);
        }

        await db.SaveChangesAsync();
        WallId = wall.Id;
        return holds;
    }

    public BlocwerkDbContext CreateContext()
    {
        var db = DbContextFactory.CreateDbContext();
        db.CurrentUserId = Guid.Empty;
        return db;
    }

    public void Dispose()
    {
        connection.Dispose();
    }
}

/// <summary>
/// Hands out contexts bound to one shared in-memory SQLite connection, mirroring the
/// production <see cref="IDbContextFactory{TContext}"/> usage in the services.
/// </summary>
public sealed class TestDbContextFactory : IDbContextFactory<BlocwerkDbContext>
{
    private readonly SqliteConnection connection;

    public TestDbContextFactory(SqliteConnection connection)
    {
        this.connection = connection;
    }

    public BlocwerkDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SqliteBlocwerkDbContext(options);
    }
}

/// <summary>
/// SQLite cannot ORDER BY a <see cref="DateTimeOffset"/>, which the production queries
/// do freely against Postgres. Storing them as ticks keeps those queries translatable
/// without bending the production model to suit the test provider.
/// </summary>
public sealed class SqliteBlocwerkDbContext : BlocwerkDbContext
{
    public SqliteBlocwerkDbContext(DbContextOptions<BlocwerkDbContext> options)
        : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        base.ConfigureConventions(builder);
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToTicksConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToTicksConverter>();
    }
}

/// <summary>Round-trips a <see cref="DateTimeOffset"/> through UTC ticks.</summary>
public sealed class DateTimeOffsetToTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToTicksConverter()
        : base(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero))
    {
    }
}
