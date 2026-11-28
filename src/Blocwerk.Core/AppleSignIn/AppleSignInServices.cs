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
        var defaults = new AppleSignInSettings();
        settings.AllowedAudiences = Configured(section, nameof(AppleSignInSettings.AllowedAudiences)) ?? defaults.AllowedAudiences;
        settings.WebAudiences = Configured(section, nameof(AppleSignInSettings.WebAudiences)) ?? defaults.WebAudiences;
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

    private static string[]? Configured(IConfigurationSection section, string key) =>
        section.GetSection(key).Get<string[]>() is { Length: > 0 } list ? list : null;
}
