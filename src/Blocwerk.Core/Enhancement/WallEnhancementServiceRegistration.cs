// <copyright file="WallEnhancementServiceRegistration.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enhancement.Steps;
using Blocwerk.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>DI registration of the one-time wall enhancement.</summary>
public static class WallEnhancementServiceRegistration
{
    /// <summary>
    /// The steps (in their order), the job and its runner (singletons on the root context factory: a run outlives the
    /// circuit that started it), the startup resumer, and the scoped service the card and the review page use.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddWallEnhancement(this IServiceCollection services)
    {
        services.AddSingleton<IWallEnhancementStep, OutlinesEnhancementStep>();
        services.AddSingleton<IWallEnhancementStep, StacksEnhancementStep>();
        services.AddSingleton<IWallEnhancementStep, CleanupEnhancementStep>();
        services.AddSingleton<IWallEnhancementStep, ColoursEnhancementStep>();
        services.AddSingleton<IWallEnhancementStep, DuplicatesEnhancementStep>();
        services.AddSingleton<IWallEnhancementStep, LinksEnhancementStep>();
        services.AddSingleton<IWallEnhancementStep, NewHoldsEnhancementStep>();
        services.AddSingleton<IWallEnhancementActorFactory, ScopedWallEnhancementActorFactory>();
        services.AddSingleton(sp => new WallEnhancementJob(
            sp.GetRequiredService<RootDbContextFactory>(),
            sp.GetRequiredService<IWallEnhancementActorFactory>(),
            sp.GetServices<IWallEnhancementStep>(),
            sp.GetRequiredService<IChangeJournal>(),
            sp.GetRequiredService<ILogger<WallEnhancementJob>>(),
            sp.GetService<IDeployBusyGate>(),
            sp.GetService<IWallEditPresence>(),
            sp.GetService<IDeployBusyEta>()));
        services.AddSingleton<WallEnhancementRunner>();
        services.AddHostedService(sp => new WallEnhancementResumer(
            sp.GetRequiredService<RootDbContextFactory>(),
            sp.GetRequiredService<WallEnhancementRunner>(),
            sp.GetRequiredService<ILogger<WallEnhancementResumer>>()));
        services.AddScoped<IWallEnhancementService, WallEnhancementService>();
        return services;
    }
}
