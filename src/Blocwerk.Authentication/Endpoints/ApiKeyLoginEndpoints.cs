using System.Threading.RateLimiting;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Authentication.Endpoints;

/// <summary>
/// <c>POST /account/api-key-login</c>: signs the caller into a normal browser session as the owner
/// of a PERSONAL API key, so automation (Playwright's <c>context.request.post</c>, which shares its
/// cookie jar with the browser pages) can drive the UI without an OAuth round-trip.
/// </summary>
/// <remarks>
/// <para><b>Off by default.</b> Unless <see cref="ApiKeyLoginSettings.Enabled"/> is set the route is
/// not mapped at all and answers 404 like any other unknown path; the handler re-checks the flag as
/// defence in depth.</para>
/// <para><b>CSRF.</b> The credential is the <c>Authorization</c> header, which a cross-site page
/// cannot attach: a form or a navigation never carries it, and a script would need a CORS preflight
/// this app never grants. The endpoint binds nothing from a form, so the antiforgery middleware has
/// nothing to validate and no exemption is needed — an attacker's page can neither sign a victim in
/// as the attacker (login CSRF) nor learn anything from the response.</para>
/// <para><b>Kiosk.</b> The path is outside <c>KioskRestrictions.AllowedPathPrefixes</c>, so a kiosk
/// device is refused with 403 by the middleware, and the validator refuses a kiosk context again.</para>
/// </remarks>
public static class ApiKeyLoginEndpoints
{
    public const string Path = "/account/api-key-login";

    public const string RateLimitPolicy = "api-key-login";

    /// <summary>Attempts allowed per client IP per <see cref="RateLimitWindow"/>, successes included.</summary>
    public const int PermitsPerWindow = 10;

    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    private const string LoggerCategory = "Blocwerk.Authentication.ApiKeyLogin";
    private const string DefaultReturnUrl = "/walls";

    public static IServiceCollection AddApiKeyLogin(this IServiceCollection services)
    {
        services.AddScoped<ApiKeyLoginValidator>();

        // "Is this a session signed in with a key?" for the account-security refusals in Core.
        services.AddScoped<IApiKeySessionContext, ApiKeySessionContext>();

        // Re-checks the key behind a key session on the cookie's own validation. PostConfigure, so it
        // chains after whatever the cookie registration (the kiosk validator) already installed.
        services.PostConfigure<CookieAuthenticationOptions>(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ApiKeySessionValidator.Configure);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(LoggerCategory)
                    .LogWarning(
                        "API key login rate limit hit from {RemoteIp}",
                        context.HttpContext.Connection.RemoteIpAddress);
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerWindow,
                    Window = RateLimitWindow,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });

        return services;
    }

    /// <summary>Maps the route when it is enabled; does nothing (the path stays a 404) otherwise.</summary>
    public static void MapApiKeyLogin(this IEndpointRouteBuilder endpoints, BlocwerkSettings settings)
    {
        if (!settings.Auth.ApiKeyLogin.Enabled)
        {
            return;
        }

        endpoints.MapPost(Path, LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);
    }

    // Query: ?returnUrl=<local url>&redirect=1. The key is read from the Authorization header ONLY —
    // nothing here binds it from the query or a form, so it can never end up in a URL or access log.
    private static async Task<IResult> LoginAsync(
        HttpContext http,
        BlocwerkSettings settings,
        ApiKeyLoginValidator validator,
        ILoggerFactory loggerFactory)
    {
        if (!settings.Auth.ApiKeyLogin.Enabled)
        {
            return Results.NotFound();
        }

        var logger = loggerFactory.CreateLogger(LoggerCategory);
        var remoteIp = http.Connection.RemoteIpAddress;
        var result = await validator.ValidateAsync(http.Request, http.RequestAborted);
        if (!result.Succeeded)
        {
            logger.LogWarning(
                "API key login refused for key {ApiKeyId} ({KeyPrefix}) from {RemoteIp}: {Reason}",
                result.ApiKeyId,
                result.KeyPrefix,
                remoteIp,
                result.FailureReason);
            return Results.Json(new { error = "Invalid API key." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        // A MARKED, key-bounded session (see ApiKeySessionSignIn), never the ordinary 8h sliding one.
        await ApiKeySessionSignIn.SignInAsync(http, result.User!, result.Key!);
        logger.LogInformation(
            "API key login: user {UserId} signed in with key {ApiKeyId} ({KeyPrefix}) from {RemoteIp}",
            result.User!.Id,
            result.ApiKeyId,
            result.KeyPrefix,
            remoteIp);

        return BuildSuccessResult(http);
    }

    /// <summary>
    /// 204 by default — a script only needs the cookie. With <c>?redirect=1</c> a 302 to the
    /// returnUrl when it is local, else to the default landing page; a non-local returnUrl is dropped
    /// exactly as the password and OAuth sign-ins drop it, never followed.
    /// </summary>
    private static IResult BuildSuccessResult(HttpContext http)
    {
        var query = http.Request.Query;
        if (query["redirect"].ToString() is not ("1" or "true"))
        {
            return Results.NoContent();
        }

        var returnUrl = query["returnUrl"].ToString();
        return Results.LocalRedirect(LocalReturnUrl.IsLocal(http, returnUrl) ? returnUrl : DefaultReturnUrl);
    }
}
