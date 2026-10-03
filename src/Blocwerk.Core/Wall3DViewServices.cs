// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core;

/// <summary>Registration of the opt-in 3D wall view.</summary>
public static class Wall3DViewServices
{
    /// <summary>Registers <see cref="IWall3DViewService"/> (scoped, like the wall services it reads through).</summary>
    public static IServiceCollection AddWall3DView(this IServiceCollection services)
    {
        services.AddScoped<IWall3DViewService, Wall3DViewService>();
        return services;
    }
}
