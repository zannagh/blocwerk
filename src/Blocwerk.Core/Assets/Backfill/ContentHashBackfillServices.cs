// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>DI registration of the content-hash backfill (design §6.2, PR 2.2).</summary>
public static class ContentHashBackfillServices
{
    /// <summary>Registers <see cref="ContentHashBackfill"/>; needs the wall-image and capture file stores.</summary>
    public static IServiceCollection AddContentHashBackfill(this IServiceCollection services)
    {
        services.AddSingleton(new ContentHashBackfillOptions());
        services.AddSingleton<ContentHashBackfill>();
        services.AddHostedService(sp => sp.GetRequiredService<ContentHashBackfill>());
        return services;
    }
}
