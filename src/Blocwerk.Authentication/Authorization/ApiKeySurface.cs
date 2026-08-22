using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// The request paths on which an API key is allowed to authenticate at all, and the scheme
/// selection that enforces it.
/// </summary>
/// <remarks>
/// An API key is a long-lived secret that lives on a Raspberry Pi bolted to a climbing wall, so it
/// must be assumed to leak. Without a path gate the key would authenticate the app's DEFAULT
/// scheme on every request — Blazor pages and the browser's own <c>/api/offline</c> routes
/// included — which would turn a device key into a full login for its owner. The key therefore
/// only ever produces a principal on the two machine-facing route families below; everywhere else
/// the bearer is simply ignored and the request stays anonymous.
/// </remarks>
public static class ApiKeySurface
{
    /// <summary>Wall-scoped machine routes: temperature, images, gallery reads.</summary>
    public const string WallApiPrefix = "/api/walls";

    /// <summary>User-scoped machine routes: the personal REST API.</summary>
    public const string UserApiPrefix = "/api/v1";

    /// <summary>Every prefix an API key may authenticate under. Deliberately not /api/offline.</summary>
    public static readonly IReadOnlyList<string> AllowedPrefixes = [WallApiPrefix, UserApiPrefix];

    /// <summary>True when the path belongs to the machine-facing API surface.</summary>
    public static bool Covers(PathString path)
    {
        foreach (var prefix in AllowedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the endpoint the request resolved to opted into authorization explicitly, i.e.
    /// carries <c>[Authorize]</c>/<c>RequireAuthorization</c> metadata.
    /// </summary>
    /// <remarks>
    /// The allowed prefixes are open by default: anything mounted under them authenticates API
    /// keys, and only the endpoint's own authorization metadata narrows that down. An endpoint
    /// without any is one nobody considered when writing it, so an API key must not be able to
    /// resolve a user through it.
    /// </remarks>
    public static bool HasExplicitAuthorization(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return false;
        }

        return endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
    }

    /// <summary>
    /// Picks the scheme the policy scheme forwards a request to. Machine callers send the same
    /// header shape as the SPA/JWT callers, so the <c>bwk_</c> prefix is what separates a
    /// long-lived API key from a short-lived JWT — but an API key is only honoured on
    /// <see cref="AllowedPrefixes"/>. Anywhere else it falls through to the cookie scheme, which
    /// cannot be satisfied by a bearer token, so the request ends up anonymous.
    /// </summary>
    public static string SelectScheme(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader)
            || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return CookieAuthenticationDefaults.AuthenticationScheme;
        }

        var bearer = authHeader["Bearer ".Length..].TrimStart();
        if (!bearer.StartsWith(ApiKey.TokenPrefix, StringComparison.Ordinal))
        {
            return JwtBearerDefaults.AuthenticationScheme;
        }

        return Covers(context.Request.Path)
            ? ApiKeyAuthenticationHandler.SchemeName
            : CookieAuthenticationDefaults.AuthenticationScheme;
    }
}
