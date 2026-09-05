using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.TopLogger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The re-sync pre-check must reconcile the most-recent session before skipping: a session we already
/// saw can grow (e.g. a mid-session PWA sync captures only the first ascents), so its later ascents
/// must still be imported. It stays cheap — only that one session is pulled, never the whole logbook —
/// and dedupes on the tick's external id so nothing double-imports.
/// </summary>
public class TopLoggerReconcileTests
{
    private const string SessionKey = "2026-09-05";

    [Fact]
    public async Task Sync_ImportsTheDelta_WhenAnAlreadySeenSessionGrew()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        var day = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        await SeedConnectionAsync(harness, lastSyncAt: day);

        // We already stored the first 2 ascents of this session.
        await SeedExistingAscentsAsync(harness, day, count: 2);

        // TopLogger now reports the session finished with 10 ascents.
        var apiClient = FakeClientForSession(day, ticksInSession: 10);
        var service = CreateService(harness, apiClient);

        var result = await service.SyncAsync(harness.Owner.Id);

        Assert.True(result.Success);
        Assert.Equal(8, result.Imported);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(10, await CountAscentsAsync(harness));

        // Only the one session was pulled — never the full logbook.
        await apiClient.DidNotReceive()
            .GetTicksAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await apiClient.Received(1)
            .GetSessionTicksAsync(harness.Owner.Id, SessionKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sync_SkipsWithoutFullPull_WhenTheLatestSessionIsUnchanged()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        // Anchored to "yesterday" so the post-sync marker (now) is unambiguously later, and the session
        // day is not after the last sync's day (so the pre-check reconciles rather than full-pulls).
        var day = DateTimeOffset.UtcNow.AddDays(-1);
        var previousSync = day.AddMinutes(30);
        await SeedConnectionAsync(harness, lastSyncAt: previousSync);

        // We already have all 5 ascents the session holds; TopLogger reports the same 5.
        await SeedExistingAscentsAsync(harness, day, count: 5);
        var apiClient = FakeClientForSession(day, ticksInSession: 5);
        var service = CreateService(harness, apiClient);

        var result = await service.SyncAsync(harness.Owner.Id);

        Assert.True(result.Success);
        Assert.Equal(0, result.Imported);
        Assert.Equal(5, result.Skipped);
        Assert.Equal(5, await CountAscentsAsync(harness));

        var connection = await LoadConnectionAsync(harness);
        Assert.Equal(TopLoggerSyncOutcome.Success, connection.LastSyncOutcome);
        Assert.NotNull(connection.LastSyncAt);
        Assert.True(connection.LastSyncAt > previousSync);

        // Reconcile probes one session; it must never fall back to a full logbook pull.
        await apiClient.DidNotReceive()
            .GetTicksAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sync_DoesNotDoubleImport_WhenReconcileRunsTwice()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        var day = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        await SeedConnectionAsync(harness, lastSyncAt: day);
        await SeedExistingAscentsAsync(harness, day, count: 2);

        var apiClient = FakeClientForSession(day, ticksInSession: 10);
        var service = CreateService(harness, apiClient);

        var first = await service.SyncAsync(harness.Owner.Id);
        var second = await service.SyncAsync(harness.Owner.Id);

        Assert.Equal(8, first.Imported);
        Assert.Equal(0, second.Imported);
        Assert.Equal(10, second.Skipped);
        Assert.Equal(10, await CountAscentsAsync(harness));
    }

    private static ITopLoggerApiClient FakeClientForSession(DateTimeOffset day, int ticksInSession)
    {
        var apiClient = Substitute.For<ITopLoggerApiClient>();
        apiClient
            .GetLatestSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new TopLoggerSessionSummary(day, SessionKey));
        apiClient
            .GetSessionTicksAsync(Arg.Any<Guid>(), SessionKey, Arg.Any<CancellationToken>())
            .Returns(BuildTicks(day, ticksInSession));
        return apiClient;
    }

    private static IReadOnlyList<TopLoggerTick> BuildTicks(DateTimeOffset day, int count)
    {
        var ticks = new List<TopLoggerTick>();
        for (var i = 1; i <= count; i++)
        {
            ticks.Add(new TopLoggerTick(
                ExternalId: $"t{i}",
                ClimbId: $"climb-{i}",
                ClimbName: $"Boulder {i}",
                ClimbType: "boulder",
                GymId: null,
                GymName: null,
                GymSlug: null,
                LoggedAt: day,
                TickType: "flash",
                TryIndex: 1,
                Ticked: true,
                Topped: true,
                Points: null,
                RawGrade: "6A",
                MappedFontGrade: "6A"));
        }

        return ticks;
    }

    private static TopLoggerImportService CreateService(WallTestHarness harness, ITopLoggerApiClient apiClient) =>
        new(
            harness.DbContextFactory,
            apiClient,
            Substitute.For<ITopLoggerTokenStore>(),
            NullLogger<TopLoggerImportService>.Instance);

    private static async Task SeedConnectionAsync(WallTestHarness harness, DateTimeOffset? lastSyncAt)
    {
        await using var db = harness.CreateContext();
        db.TopLoggerConnections.Add(new TopLoggerConnection
        {
            UserId = harness.Owner.Id,
            AccessTokenProtected = "cipher-access",
            RefreshTokenProtected = "cipher-refresh",
            LastSyncAt = lastSyncAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedExistingAscentsAsync(WallTestHarness harness, DateTimeOffset day, int count)
    {
        await using var db = harness.CreateContext();
        for (var i = 1; i <= count; i++)
        {
            db.ExternalAscents.Add(new ExternalAscent
            {
                UserId = harness.Owner.Id,
                Source = ExternalSource.TopLogger,
                ExternalId = $"t{i}",
                ClimbName = $"Boulder {i}",
                LoggedAt = day,
                Type = AttemptType.Flash,
                MappedGrade = "6A",
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<int> CountAscentsAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        return await db.ExternalAscents.CountAsync(a => a.UserId == harness.Owner.Id);
    }

    private static async Task<TopLoggerConnection> LoadConnectionAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        return await db.TopLoggerConnections.AsNoTracking().FirstAsync(c => c.UserId == harness.Owner.Id);
    }
}
