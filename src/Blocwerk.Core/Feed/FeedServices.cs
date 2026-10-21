// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Registers the live-update feed: storage, reads, retention and the lag gauge (PR 1.1); the interceptor, the wake signal
/// and the hub (PR 1.2a); the mappers (PR 1.2b, 1.2c); the in-process commit observers (PR 1.8).
/// </summary>
public static class FeedServices
{
    public static IServiceCollection AddWallFeed(this IServiceCollection services)
    {
        // A singleton on the session-less factory: the feed's rows carry no kiosk scoping, and every reader checks access
        // to the wall before it reads (catch-up endpoint, hub).
        services.AddSingleton<IWallFeed>(sp => new WallFeed(
            () => sp.GetRequiredService<RootDbContextFactory>().CreateDbContext(),
            sp.GetRequiredService<ILogger<WallFeed>>()));

        // The write side: the mappers (FeedMappers) and the interceptor that runs them, added to the
        // context options in CoreServices. Singletons: they hold no per-save state outside their per-context tables.
        services.AddSingleton(_ => new FeedMapperRegistry(FeedMappers.Create()));
        services.AddSingleton<IFeedSignal, InProcessFeedSignal>();

        // The own-write path (PR 1.8): committed rows reach the circuit caches synchronously, before the save returns.
        services.AddSingleton<FeedCommits>();
        services.AddSingleton<FeedInterceptor>();

        // The fan-out. Hydration reads through the session-less factory, like the feed: the subscriber's viewer, not a
        // kiosk stamp, decides what leaves the server.
        services.AddSingleton(sp => new WallFeedHub(
            sp.GetRequiredService<IWallFeed>(),
            sp.GetRequiredService<IFeedSignal>(),
            () => sp.GetRequiredService<RootDbContextFactory>().CreateDbContext(),
            sp.GetRequiredService<ILogger<WallFeedHub>>()));
        services.AddSingleton<IWallFeedHub>(sp => sp.GetRequiredService<WallFeedHub>());
        services.AddHostedService(sp => sp.GetRequiredService<WallFeedHub>());
        services.AddHostedService<WallChangeRetentionService>();
        services.AddHostedService<FeedLagCollector>();
        return services;
    }
}
