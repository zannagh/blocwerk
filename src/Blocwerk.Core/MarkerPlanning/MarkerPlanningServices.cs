// <copyright file="MarkerPlanningServices.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>DI registration for the marker planner.</summary>
public static class MarkerPlanningServices
{
    /// <summary>Registers <see cref="IMarkerPlanService"/> (scoped, like the other wall services).</summary>
    public static IServiceCollection AddMarkerPlanning(this IServiceCollection services)
    {
        services.AddScoped<IMarkerPlanService, MarkerPlanService>();
        return services;
    }
}
