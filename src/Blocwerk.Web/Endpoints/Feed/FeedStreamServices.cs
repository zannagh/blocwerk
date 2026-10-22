// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>Registers the feed stream's shared state and the feed routes' rate limit.</summary>
public static class FeedStreamServices
{
    public static IServiceCollection AddFeedStream(this IServiceCollection services)
    {
        services.AddSingleton(new FeedStreamOptions());
        services.AddSingleton<FeedStreamLimits>();
        services.AddSingleton<FeedStreamHost>();
        services.AddFeedRateLimit();
        return services;
    }
}
