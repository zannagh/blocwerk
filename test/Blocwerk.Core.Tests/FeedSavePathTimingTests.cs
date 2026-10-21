// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A measurement, not a check: the hold-edit save (<see cref="IWallService.UpdateHoldAsync"/>) on Postgres with the
/// production interceptors, then with the feed interceptor as PR 1.2a ships it (no mapper), then with a stand-in hold
/// mapper (what PR 1.2b adds: one row per save). Runs only with <c>BLOCWERK_FEED_BENCH=1</c> and a Postgres server; the
/// configurations are interleaved rotated so drift and position hit all of them alike.
/// </summary>
[Collection(PostgresFeedCollection.Name)]
public sealed class FeedSavePathTimingTests
{
    private const int Warmup = 30;
    private readonly ITestOutputHelper output;

    public FeedSavePathTimingTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [SkippableFact]
    public async Task HoldEditSave_BeforeAndAfterTheFeedInterceptor()
    {
        Skip.IfNot(PostgresTestDatabase.Enabled && Environment.GetEnvironmentVariable("BLOCWERK_FEED_BENCH") == "1", "a measurement; set BLOCWERK_FEED_BENCH=1");
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_FEED_BENCH_ROUNDS"), out var r) ? r : 300;
        using var harness = new WallTestHarness(PostgresTestDatabase.Create());
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];

        // Pooled, as in production (the test databases are unpooled so they can be dropped).
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(harness.DbContextFactory.ConnectionString) { Pooling = true }.ConnectionString;
        var signal = new InProcessFeedSignal();
        var configs = new (string Name, IWallService Service)[]
        {
            ("main (journal + domain change)", Service(harness, cs)),
            ("1.2a (+ feed interceptor, no mapper)", Service(harness, cs, Feed(signal))),
            ("1.2b preview (+ one hold row per save)", Service(harness, cs, Feed(signal, new ProbeHoldMapper()))),
        };
        var samples = configs.Select(_ => new List<double>()).ToArray();

        for (var round = 0; round < Warmup + rounds; round++)
        {
            for (var i = 0; i < configs.Length; i++)
            {
                var c = (round + i) % configs.Length; // rotate, so no configuration always runs first
                var watch = Stopwatch.StartNew();
                await configs[c].Service.UpdateHoldAsync(hold.Id, new HoldEdit { X = 0.1 + (round % 50 * 0.001), Y = 0.2, Radius = 0.02 });
                if (round >= Warmup)
                {
                    samples[c].Add(watch.Elapsed.TotalMilliseconds);
                }
            }

            while (signal.Wakes.TryRead(out _))
            {
            }
        }

        Npgsql.NpgsqlConnection.ClearAllPools();

        for (var c = 0; c < configs.Length; c++)
        {
            var sorted = samples[c].Order().ToList();
            output.WriteLine($"{configs[c].Name}: median {sorted[sorted.Count / 2]:F2} ms, p95 {sorted[(int)(sorted.Count * 0.95)]:F2} ms, mean {sorted.Average():F2} ms (n={sorted.Count})");
        }
    }

    private static FeedInterceptor Feed(IFeedSignal signal, params IFeedMapper[] mappers) =>
        new(new FeedMapperRegistry(mappers), signal, NullLogger<FeedInterceptor>.Instance);

    private static IWallService Service(WallTestHarness harness, string cs, params IInterceptor[] extra)
    {
        var journal = new ChangeJournal(() => TestDb.Create(cs));
        IInterceptor[] interceptors =
            [new DomainChangeInterceptor(new DomainChangeNotifier()), new ChangeJournalInterceptor(journal), .. extra];
        return new WallService(new InterceptedDbContextFactory(cs, interceptors), harness.CurrentUser, harness.HoldDetection, harness.ActivityLog, NullLogger<WallService>.Instance);
    }
}
