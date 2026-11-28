// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.Authentication.Apple;

/// <summary>Registers web Sign in with Apple. Always registered; it stays off while the four settings are missing.</summary>
public static class AppleWebServices
{
    public static IHostApplicationBuilder AddAppleWebSignIn(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton(AppleWebSettings.FromConfiguration(builder.Configuration));
        builder.Services.AddSingleton<AppleClientSecret>();
        builder.Services.AddSingleton<AppleOAuthCookie>();
        builder.Services.AddSingleton<AppleCallbackStore>();
        builder.Services.AddHttpClient(AppleTokenClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 256 * 1024;
        });
        builder.Services.AddSingleton<AppleTokenClient>();
        builder.Services.AddScoped<AppleWebAccounts>();
        builder.Services.AddScoped<AppleWebSignIn>();
        return builder;
    }
}
