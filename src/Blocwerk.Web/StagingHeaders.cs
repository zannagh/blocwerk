// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web;

/// <summary>
/// The staging copy (<c>Blocwerk:Staging</c>) keeps crawlers out: every response, assets and API included, says
/// <c>X-Robots-Tag: noindex, nofollow</c>. The pages add the matching <c>robots</c> meta in <c>BlocwerkApp</c>.
/// </summary>
public static class StagingHeaders
{
    public const string RobotsTag = "noindex, nofollow";

    /// <summary>Adds the header to every response when <paramref name="staging"/> is on; adds nothing to the pipeline otherwise.</summary>
    public static IApplicationBuilder UseStagingHeaders(this IApplicationBuilder app, bool staging)
    {
        if (!staging)
        {
            return app;
        }

        return app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Robots-Tag"] = RobotsTag;
            await next();
        });
    }
}
