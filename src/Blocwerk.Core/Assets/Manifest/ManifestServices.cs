// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets.Partition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Blocwerk.Core.Assets.Manifest;

public static class ManifestServices
{
    /// <summary>
    /// Registers the manifest (builder, cache, service) and the cache-partition resolver. The partition key
    /// (<see cref="ICachePartitionKey"/>) comes from the authentication layer, where the DataProtection key ring is.
    /// </summary>
    public static IServiceCollection AddWallManifest(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WallManifestCache>();
        services.AddSingleton<WallManifestBuilder>();
        services.AddScoped<WallManifestService>();
        services.AddScoped<ICachePartitionResolver, CachePartitionResolver>();
        return services;
    }
}
