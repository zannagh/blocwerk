// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Apple;
using Blocwerk.Authentication.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// Where Apple returns a web Sign in with Apple: a cross-site form POST (<c>response_mode=form_post</c>) carrying the
/// state, the code, and on the first authorization the user's name.
/// </summary>
/// <remarks>
/// <para>The antiforgery token cannot apply: the POST comes from appleid.apple.com, and its protection is the state
/// instead, checked twice. The server-side state store must know it as an Apple round trip, and the browser must carry
/// the <see cref="AppleOAuthCookie"/> sealing the same state, so a code from somebody else's round trip is refused.</para>
/// <para>The answer is a 303 into the same <c>/signing-in</c> → <c>/account/callback</c> GET chain the other providers
/// use. That navigation carries the Lax session and link cookies the POST could not, and nothing in the account
/// cookies needs to relax for it.</para>
/// </remarks>
public class AppleCallbackController(AppleWebSignIn apple, RedirectUriProvider redirects) : ControllerBase
{
    [HttpPost(AppleWebSettings.CallbackPath)]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [Consumes("application/x-www-form-urlencoded")]
    public IActionResult Callback(
        [FromForm(Name = "state")] string? state,
        [FromForm(Name = "code")] string? code,
        [FromForm(Name = "user")] string? user,
        [FromForm(Name = "error")] string? error)
    {
        if (!apple.IsEnabled)
        {
            return SeeOther("/account/login?error=auth_failed");
        }

        var knownState = !string.IsNullOrEmpty(state)
            && redirects.GetRedirectUri(state, out var round)
            && round.Provider == AppleWebSettings.Provider;
        if (!knownState)
        {
            Log.Warning("[Web Authentication] Apple callback refused: unknown state.");
            return SeeOther("/account/login?error=auth_failed");
        }

        // Apple reports a cancel as error=user_cancelled_authorize, with no code.
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            apple.AcceptCallback(Request, Response, state!, null, null);
            return SeeOther("/account/login?error=cancelled");
        }

        if (!apple.AcceptCallback(Request, Response, state!, code, user))
        {
            Log.Warning("[Web Authentication] Apple callback refused: the browser's round-trip cookie is missing or stale.");
            return SeeOther("/account/login?error=auth_failed");
        }

        return SeeOther($"/signing-in?state={Uri.EscapeDataString(state!)}&code={Uri.EscapeDataString(code)}");
    }

    // A 303, so the browser follows with a GET whatever it does with a 302 after a POST. Always a local literal path.
    private StatusCodeResult SeeOther(string localPath)
    {
        Response.Headers.Location = localPath;
        return StatusCode(StatusCodes.Status303SeeOther);
    }
}
