// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Configuration;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The origin a link handed to someone else starts with: the configured server address (<c>Blocwerk:Server:Url</c>,
/// env <c>SERVER__URL</c>) when one is set to a public address, else the request's own origin, which is what the web
/// pages use (<c>NavigationManager.BaseUri</c>) and what the forwarded-headers middleware resolves behind the proxy.
/// </summary>
/// <remarks>
/// The setting defaults to <c>https://localhost:5001</c>, which is never a link to hand out, so a loopback or missing
/// address falls back to the request.
/// </remarks>
public static class PublicBaseUrl
{
    /// <summary>The origin, without a trailing slash.</summary>
    public static string Of(BlocwerkSettings settings, HttpRequest request)
    {
        if (Uri.TryCreate(settings.Server?.Url, UriKind.Absolute, out var configured)
            && !configured.IsLoopback
            && configured.Scheme is "https" or "http")
        {
            return configured.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        return $"{request.Scheme}://{request.Host}{request.PathBase}";
    }
}
