// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.Core.AppleSignIn;

/// <summary>Registers native Sign in with Apple: the settings, Apple's key cache, the validator and the service.</summary>
public static class AppleSignInServices
{
    public static IHostApplicationBuilder AddAppleSignIn(this IHostApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(AppleSignInSettings.SectionName);
        var settings = section.Get<AppleSignInSettings>() ?? new AppleSignInSettings();

        // The binder appends configured array items to the default ones; a configured list is meant to replace them.
        var audiences = section.GetSection(nameof(AppleSignInSettings.AllowedAudiences)).Get<string[]>();
        settings.AllowedAudiences = audiences is { Length: > 0 } ? audiences : new AppleSignInSettings().AllowedAudiences;
        builder.Services.AddSingleton(settings);
        builder.Services.AddHttpClient(AppleKeyCache.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 256 * 1024;
        });
        builder.Services.AddSingleton<IAppleKeySource, AppleKeyCache>();
        builder.Services.AddSingleton<IAppleIdentityTokenValidator, AppleIdentityTokenValidator>();
        builder.Services.AddScoped<AppleNonceStore>();
        builder.Services.AddScoped<IAppleSignInService, AppleSignInService>();
        return builder;
    }
}
