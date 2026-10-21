// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Pulse;

/// <summary>Registers the ephemeral job-progress channel (design §5.6, PR 1.7) and the interceptor that feeds it.</summary>
public static class PulseServices
{
    public static IServiceCollection AddWallPulse(this IServiceCollection services)
    {
        // Singletons: one process-wide channel (single instance, like the feed's in-process signal), and an interceptor
        // that holds no per-save state outside its per-context tables. CoreServices adds the interceptor to the options.
        services.AddSingleton<WallPulseHub>();
        services.AddSingleton<IWallPulse>(sp => sp.GetRequiredService<WallPulseHub>());
        services.AddSingleton<WallPulseInterceptor>();
        return services;
    }
}
