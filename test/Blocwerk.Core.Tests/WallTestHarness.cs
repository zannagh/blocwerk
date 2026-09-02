using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
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
    private readonly string betaVideoDir;
    private readonly string wallImageDir;

    public WallTestHarness()
    {
        // A NAMED shared-cache database rather than a plain ":memory:" one, so every context the
        // factory hands out opens its OWN connection to it — which is what IDbContextFactory means
        // in production. Handing several contexts the SAME SqliteConnection makes creating them
        // concurrently a race: EF registers its user functions on the connection as each context
        // initialises, and doing that while another thread is querying fails with SQLITE_BUSY.
        // This connection is held open only to keep the database alive for the harness's lifetime.
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(connectionString);
        connection.Open();

        betaVideoDir = Path.Combine(Path.GetTempPath(), "blocwerk-beta-tests", Guid.NewGuid().ToString("N"));
        wallImageDir = Path.Combine(Path.GetTempPath(), "blocwerk-wall-image-tests", Guid.NewGuid().ToString("N"));

        Owner = new User { Identifier = "owner@test", DisplayName = "Owner" };
        DbContextFactory = new TestDbContextFactory(connectionString);

        using (var db = DbContextFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        ActingUser = Owner;
        CurrentUser = Substitute.For<ICurrentUserService>();
        CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(ActingUser));

        ActivityLog = Substitute.For<IActivityLogService>();
        HoldDetection = Substitute.For<IHoldDetectionService>();
        ImageAlignment = Substitute.For<IImageAlignmentService>();

        WallService = new WallService(DbContextFactory, CurrentUser, HoldDetection, ImageAlignment, ActivityLog, NullLogger<WallService>.Instance);
        BoulderService = new BoulderService(DbContextFactory, CurrentUser, ActivityLog, NullLogger<BoulderService>.Instance);
        AttemptService = new AttemptService(DbContextFactory, CurrentUser, ActivityLog, NullLogger<AttemptService>.Instance);
        FeedbackService = new BoulderFeedbackService(DbContextFactory, CurrentUser, NullLogger<BoulderFeedbackService>.Instance);
        SegmentService = new WallSegmentService(DbContextFactory, CurrentUser, NullLogger<WallSegmentService>.Instance);
        SessionService = new Blocwerk.Core.Services.SessionService(DbContextFactory, CurrentUser, NullLogger<Blocwerk.Core.Services.SessionService>.Instance);
        var betaSettings = new BlocwerkSettings();
        betaSettings.BetaVideo.StoragePath = betaVideoDir;
        var betaStorage = new FileSystemBetaVideoStorage(betaSettings);
        BetaVideoService = new BetaVideoService(DbContextFactory, CurrentUser, ActivityLog, betaStorage, NullLogger<BetaVideoService>.Instance);

        var imageSettings = new BlocwerkSettings();
        imageSettings.WallImage.StoragePath = wallImageDir;
        WallImageStorage = new FileSystemWallImageStorage(imageSettings);
        WallImageService = new WallImageService(DbContextFactory, WallImageStorage, NullLogger<WallImageService>.Instance);
        WallTemperatureService = new WallTemperatureService(DbContextFactory, NullLogger<WallTemperatureService>.Instance);
        ApiKeyService = new ApiKeyService(DbContextFactory, NullLogger<ApiKeyService>.Instance);
        PasswordService = new PasswordService();
        KioskService = new KioskService(DbContextFactory, CurrentUser, PasswordService, NullLogger<KioskService>.Instance);
    }

    public User Owner { get; }

    /// <summary>
    /// Whom <see cref="CurrentUser"/> resolves to. Defaults to <see cref="Owner"/>; assign a user from
    /// <see cref="AddMemberAsync"/> to exercise a service call as somebody else.
    /// </summary>
    public User ActingUser { get; set; }

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

    public IBetaVideoService BetaVideoService { get; }

    public IWallImageStorage WallImageStorage { get; }

    public IWallImageService WallImageService { get; }

    public IWallTemperatureService WallTemperatureService { get; }

    public IApiKeyService ApiKeyService { get; }

    public IPasswordService PasswordService { get; }

    public IKioskService KioskService { get; }

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

    /// <summary>Adds a second user to the seeded wall with the given role and returns them.</summary>
    public async Task<User> AddMemberAsync(string identifier, WallRole role)
    {
        await using var db = CreateContext();

        var user = new User { Identifier = identifier, DisplayName = identifier };
        db.Users.Add(user);
        db.WallMembers.Add(new WallMember { WallId = WallId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync();
        return user;
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
        if (Directory.Exists(betaVideoDir))
        {
            Directory.Delete(betaVideoDir, recursive: true);
        }

        if (Directory.Exists(wallImageDir))
        {
            Directory.Delete(wallImageDir, recursive: true);
        }
    }
}

/// <summary>
/// Hands out contexts over their own connections to one shared in-memory SQLite database,
/// mirroring the production <see cref="IDbContextFactory{TContext}"/> usage in the services.
/// </summary>
/// <remarks>
/// A connection STRING rather than a connection, deliberately. Production callers are free to
/// create contexts from more than one thread — the variant cache warmer does exactly that — and a
/// factory that handed every context the same <see cref="SqliteConnection"/> could not honour it:
/// two contexts initialising at once register user functions on that one connection while the
/// other thread is mid-query, which SQLite answers with SQLITE_BUSY. Independent connections to a
/// named shared-cache database behave the way independent Postgres connections do.
/// </remarks>
public sealed class TestDbContextFactory : IDbContextFactory<BlocwerkDbContext>
{
    private readonly string connectionString;

    public TestDbContextFactory(string connectionString)
    {
        this.connectionString = connectionString;
    }

    /// <summary>
    /// A connection string for a private in-memory database. Named uniquely so tests running in
    /// parallel cannot see each other's rows, and shared-cache so that several connections to it
    /// address the same database. It lives only as long as a connection to it stays open.
    /// </summary>
    public static string IsolatedDatabase() =>
        $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    public BlocwerkDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseSqlite(connectionString)
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
