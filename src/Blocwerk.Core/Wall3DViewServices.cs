// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core;

/// <summary>Registration of the opt-in 3D wall view.</summary>
public static class Wall3DViewServices
{
    /// <summary>
    /// Registers <see cref="IWall3DViewService"/> (scoped, like the wall services it reads through) and its process-wide
    /// <see cref="Wall3DViewCache"/>, bounded by <see cref="Wall3DViewCacheSettings"/> from the app configuration.
    /// </summary>
    public static IServiceCollection AddWall3DView(this IServiceCollection services)
    {
        services.AddSingleton(sp => new Wall3DViewCache(
            Wall3DViewCacheSettings.Bind(sp.GetService<IConfiguration>()), sp.GetService<ILogger<Wall3DViewCache>>()));
        services.AddScoped<IWall3DViewService, Wall3DViewService>();
        return services;
    }
}
