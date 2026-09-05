using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.TopLogger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Every sync attempt records when it ran and whether it worked, even runs that pull no new data or
/// fail before importing — so the profile card can always show the last attempt and its outcome.
/// </summary>
public class TopLoggerSyncStatusTests
{
    [Fact]
    public async Task Sync_RecordsAttemptAndFailure_WhenTheFetchThrows()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        await SeedConnectionAsync(harness, lastSyncAt: null);

        var apiClient = Substitute.For<ITopLoggerApiClient>();
        apiClient
            .GetTicksAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TopLoggerTick>>(_ => throw new InvalidOperationException("network down"));

        var service = CreateService(harness, apiClient);

        var result = await service.SyncAsync(harness.Owner.Id);

        Assert.False(result.Success);

        var connection = await LoadConnectionAsync(harness);
        Assert.NotNull(connection.LastSyncAttemptedAt);
        Assert.Equal(TopLoggerSyncOutcome.Failed, connection.LastSyncOutcome);
        Assert.Equal("network down", connection.LastError);

        // A failed attempt must NOT advance the last-SUCCESSFUL-sync marker.
        Assert.Null(connection.LastSyncAt);
    }

    [Fact]
    public async Task Sync_RecordsAttemptAndSuccess_WhenNothingIsNew()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        var previousSync = DateTimeOffset.UtcNow.AddDays(-3);
        await SeedConnectionAsync(harness, lastSyncAt: previousSync);

        var apiClient = Substitute.For<ITopLoggerApiClient>();

        // No climb-days at all -> the re-sync pre-check skips the full pull as "nothing new".
        apiClient
            .GetLatestSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TopLoggerSessionSummary?)null);

        var service = CreateService(harness, apiClient);

        var result = await service.SyncAsync(harness.Owner.Id);

        Assert.True(result.Success);
        Assert.Equal(0, result.Imported);

        var connection = await LoadConnectionAsync(harness);
        Assert.NotNull(connection.LastSyncAttemptedAt);
        Assert.Equal(TopLoggerSyncOutcome.Success, connection.LastSyncOutcome);
        Assert.Null(connection.LastError);

        // A no-new-data run still counts as a successful sync and advances the marker.
        Assert.NotNull(connection.LastSyncAt);
        Assert.True(connection.LastSyncAt > previousSync);

        // The full pull was never attempted, since the pre-check short-circuited. With no session at all
        // there is nothing to reconcile either.
        await apiClient.DidNotReceive()
            .GetTicksAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await apiClient.DidNotReceive()
            .GetSessionTicksAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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

    private static async Task<TopLoggerConnection> LoadConnectionAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        return await db.TopLoggerConnections.AsNoTracking().FirstAsync(c => c.UserId == harness.Owner.Id);
    }
}
