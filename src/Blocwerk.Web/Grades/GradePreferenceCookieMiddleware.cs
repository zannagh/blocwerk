// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Components.Endpoints;

namespace Blocwerk.Web.Grades;

/// <summary>
/// Leaves <see cref="GradePreferenceCookie"/> on every Server page response: the viewer's preference for a signed-in
/// person, none for a kiosk session (resting or acting as a member) or for nobody. Pages only (a GET routed to a Razor
/// component), so the API, the circuit, static files and the offline client's shell never touch it.
/// </summary>
public static class GradePreferenceCookieMiddleware
{
    /// <summary>Adds the cookie step; after the kiosk middleware, which primes the kiosk context.</summary>
    /// <param name="app">The app.</param>
    public static void UseGradePreferenceCookie(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (IsServerPage(context))
            {
                GradePreferenceCookie.Apply(context, await ValueAsync(context));
            }

            await next();
        });
    }

    /// <summary>True for a page navigation: a GET whose endpoint is a Razor component page.</summary>
    /// <param name="context">The request.</param>
    /// <returns>True when the response is a Server page.</returns>
    internal static bool IsServerPage(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method)
        && context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null;

    private static async Task<string?> ValueAsync(HttpContext context)
    {
        var kiosk = context.RequestServices.GetService<IKioskContext>();
        if (kiosk is not null)
        {
            await kiosk.InitializeAsync();
            if (kiosk.IsKiosk)
            {
                return GradePreferenceCookie.ValueFor(isKiosk: true, preferFont: null);
            }
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return GradePreferenceCookie.ValueFor(isKiosk: false, preferFont: null);
        }

        try
        {
            var user = await context.RequestServices.GetRequiredService<ICurrentUserService>().GetCurrentUserAsync();
            return GradePreferenceCookie.ValueFor(isKiosk: false, preferFont: user.PreferFontGrades);
        }
        catch (UnauthorizedAccessException)
        {
            return GradePreferenceCookie.ValueFor(isKiosk: false, preferFont: null);
        }
    }
}
