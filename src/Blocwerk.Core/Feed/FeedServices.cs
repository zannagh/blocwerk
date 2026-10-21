// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>Registers the live-update feed (PR 1.1: storage, reads, retention and the lag gauge; nothing writes rows yet).</summary>
public static class FeedServices
{
    public static IServiceCollection AddWallFeed(this IServiceCollection services)
    {
        // A singleton on the session-less factory: the feed's rows carry no kiosk scoping, and every reader checks access
        // to the wall before it reads (catch-up endpoint, hub).
        services.AddSingleton<IWallFeed>(sp => new WallFeed(
            () => sp.GetRequiredService<RootDbContextFactory>().CreateDbContext(),
            sp.GetRequiredService<ILogger<WallFeed>>()));
        services.AddHostedService<WallChangeRetentionService>();
        services.AddHostedService<FeedLagCollector>();
        return services;
    }
}
