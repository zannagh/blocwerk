using Microsoft.AspNetCore.Diagnostics;

namespace Blocwerk.Web;

/// <summary>
/// Turns the one exception a signed-in browser can hit through no fault of its own — a cookie that
/// is cryptographically perfect but names nobody who may sign in — into a sign-in prompt instead of
/// a bare 500.
/// </summary>
/// <remarks>
/// The case is real and self-inflicted: an account merged away (or deleted) on another device leaves
/// every other tab holding a valid cookie that resolves to nothing. Those requests pass
/// <c>[Authorize]</c>, throw out of resolution, and — with no handler in the pipeline — 500 on every
/// reload, with nothing on screen to suggest that signing in again would fix it.
/// <para>
/// Deliberately narrow. Only <see cref="UnauthorizedAccessException"/> is turned into a redirect,
/// only for browser navigations (never for <c>/api</c>, whose endpoints already map it themselves),
/// and every other exception keeps the 500 and the log entry it already had. This handler is a
/// backstop for the static-SSR half of the app; a Blazor circuit's own exceptions never reach the
/// HTTP pipeline, so the pages that resolve a user still guard their own calls.
/// </para>
/// </remarks>
public static class UnresolvableSessionHandler
{
    /// <summary>Where a session that can no longer be resolved is sent to start over.</summary>
    private const string SignInPath = "/account/login?error=session_expired";

    public static void UseUnresolvableSessionRedirect(this WebApplication app)
    {
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = context =>
            {
                var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;

                if (error is UnauthorizedAccessException
                    && !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect(SignInPath);
                    return Task.CompletedTask;
                }

                // Everything else is a real failure and stays one.
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            },
        });
    }
}
