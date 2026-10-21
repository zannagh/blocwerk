// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>DI registration of the content-hash backfill (design §6.2, PR 2.2).</summary>
public static class ContentHashBackfillServices
{
    /// <summary>
    /// Registers <see cref="ContentHashBackfill"/> with its options from the <see cref="ContentHashBackfillOptions.Section"/>
    /// section; needs the wall-image and capture file stores.
    /// </summary>
    public static IServiceCollection AddContentHashBackfill(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection(ContentHashBackfillOptions.Section).Get<ContentHashBackfillOptions>() ?? new ContentHashBackfillOptions());
        services.AddSingleton<ContentHashBackfill>();
        services.AddHostedService(sp => sp.GetRequiredService<ContentHashBackfill>());
        return services;
    }
}
