using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Keeps a browser session that was signed in with an API key tied to that key, in the cookie
/// handler's <see cref="CookieAuthenticationEvents.OnValidatePrincipal"/>: at most every
/// <see cref="ApiKeySessionSignIn.RecheckInterval"/> the key and its owner are re-checked, and a
/// revoked, expired or re-scoped key, a deleted or locked owner, a user taken off the allow-list or
/// the feature switched off ends the session on its next request.
/// </summary>
/// <remarks>
/// Sessions without the <c>amr=apikey</c> claim leave on the first line, untouched. The last-check
/// time rides in the ticket's properties; re-stamping it re-issues the cookie with the SAME absolute
/// expiry (IssuedUtc moves to now, ExpiresUtc stays), so the cookie handler's renewal — which keeps
/// the issued-to-expiry span — cannot stretch the session.
/// </remarks>
public static class ApiKeySessionValidator
{
    /// <summary>Wires this validator into the cookie handler's options, after any existing one.</summary>
    public static void Configure(CookieAuthenticationOptions options)
    {
        var inner = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            await inner(context);
            await ValidateAsync(context);
        };
    }

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (!ApiKeySessionClaims.IsApiKeySession(principal))
        {
            return;
        }

        var keyId = ApiKeySessionClaims.ReadApiKeyId(principal);
        var userId = ApiKeySessionClaims.ReadUserId(principal);
        if (keyId is null || userId is null)
        {
            await RejectAsync(context, keyId, "malformed key session");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!ApiKeySessionSignIn.IsRecheckDue(context.Properties, now))
        {
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<ApiKeyLoginValidator>();
        if (!await validator.IsSessionStillValidAsync(keyId.Value, userId.Value, context.HttpContext.RequestAborted))
        {
            await RejectAsync(context, keyId, "key or owner no longer qualifies");
            return;
        }

        ApiKeySessionSignIn.StampChecked(context.Properties, now);
        context.Properties.IssuedUtc = now;
        context.ShouldRenew = true;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context, Guid? keyId, string reason)
    {
        context.HttpContext.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Blocwerk.Authentication.ApiKeyLogin")
            .LogWarning("API key session for key {ApiKeyId} ended: {Reason}", keyId, reason);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
