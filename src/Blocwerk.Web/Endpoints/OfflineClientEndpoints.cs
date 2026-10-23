// <copyright file="OfflineClientEndpoints.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The offline gym client's shell (Blocwerk.Client, plan 2026-10-22 §1): every path under <c>/v/</c> that names no
/// file gets the client's processed <c>index.html</c>, so the client's own router opens it. The client's files
/// themselves (<c>/v/_framework/…</c>, <c>/v/css/…</c>) are static web assets, answered earlier by
/// <c>MapStaticAssets</c>; a missing one is a plain 404, never the shell.
/// </summary>
/// <remarks>
/// Nothing links here yet and the service worker does not redirect here until PR 3.9, so the Server pages are
/// unchanged. The shell is the same bytes for every caller and carries no identity, so it is anonymous; whatever the
/// client later reads goes through the endpoints' own authorisation. It is revalidated on every load
/// (<c>no-cache</c>): it names the fingerprinted framework, so a stale shell would boot an old client.
/// </remarks>
public static class OfflineClientEndpoints
{
    /// <summary>The client's base path, as its index.html's base element names it.</summary>
    public const string BasePath = "/v/";

    /// <summary>The shell inside the web root, where the client's static web assets put it.</summary>
    public const string ShellFile = "v/index.html";

    /// <summary>Maps <c>/v</c> (a redirect to <c>/v/</c>) and the shell fallback under <c>/v/</c>.</summary>
    /// <param name="app">The app, after <c>MapStaticAssets</c>.</param>
    public static void MapOfflineClient(this WebApplication app)
    {
        // The client's base is "/v/": without the slash, the router has no base to resolve the address against. A
        // middleware, not a "/v" route: routing ignores a trailing slash, so that route would also catch "/v/" itself.
        app.Use(async (context, next) =>
        {
            if (string.Equals(context.Request.Path.Value, "/v", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect(BasePath + context.Request.QueryString);
                return;
            }

            await next(context);
        });

        app.MapFallbackToFile("/v/{**path:nonfile}", ShellFile, new StaticFileOptions
        {
            OnPrepareResponse = context =>
                context.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache",
        }).AllowAnonymous();
    }
}
