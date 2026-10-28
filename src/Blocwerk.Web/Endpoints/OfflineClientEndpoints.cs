// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The address rules of the offline gym client under <c>/v/</c> (Blocwerk.Client, plan 2026-10-22 §1). Its shell is
/// <c>OfflineClientShellController</c>; its files are static assets.
/// </summary>
public static class OfflineClientEndpoints
{
    /// <summary>The client's base path, as its shell's base element names it.</summary>
    public const string BasePath = "/v/";

    /// <summary>
    /// Installs the redirect middleware: <c>/v</c>, and any path that is <c>/v/…</c> only case-insensitively
    /// (<c>/V/walls/…</c>), go to <c>/v/</c> + the rest + the query. The client's router resolves the address against its
    /// base exactly; without the slash, or in another case, it has none and the boot throws. A middleware, not a route:
    /// routing ignores a trailing slash and case, so a "/v" route would also catch "/v/" itself.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapOfflineClient(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (Canonical(context.Request.Path.Value) is { } target)
            {
                await Results.LocalRedirect(target + context.Request.QueryString).ExecuteAsync(context);
                return;
            }

            await next(context);
        });
    }

    /// <summary>The canonical client path for <paramref name="path"/>, or null when it is canonical or not the client's.</summary>
    /// <param name="path">The request path.</param>
    /// <returns><c>/v/</c> + the rest, or null.</returns>
    internal static string? Canonical(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (string.Equals(path, "/v", StringComparison.OrdinalIgnoreCase))
        {
            return BasePath;
        }

        var underBase = path.StartsWith(BasePath, StringComparison.OrdinalIgnoreCase);

        // Leading slashes and backslashes of the rest are dropped: "/V//host" must not become the scheme-relative "//host".
        return underBase && !path.StartsWith(BasePath, StringComparison.Ordinal)
            ? BasePath + path[BasePath.Length..].TrimStart('/', '\\')
            : null;
    }
}
