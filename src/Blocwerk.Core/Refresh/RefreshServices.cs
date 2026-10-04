// <copyright file="RefreshServices.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Refresh;

/// <summary>DI registration of "Update panels + 3D".</summary>
public static class RefreshServices
{
    public static IServiceCollection AddWallRefresh(this IServiceCollection services)
    {
        services.AddSingleton<WallRefreshQueue>();
        services.AddSingleton(_ => new WallRefreshLocks());
        services.AddSingleton<PanelPhotoPicker>();
        services.AddSingleton<ICaptureVideoJoiner, CaptureVideoJoiner>();
        services.AddSingleton<IWallRefreshActorFactory, ScopedWallRefreshActorFactory>();
        services.AddSingleton<WallRefreshProcessor>();
        services.AddHostedService<WallRefreshWorker>();
        services.AddScoped<IWallRefreshService, WallRefreshService>();
        return services;
    }
}
