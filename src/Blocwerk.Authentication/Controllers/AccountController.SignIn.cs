// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Authentication.Controllers;

/// <summary>The plain sign-in tail of <c>/account/callback</c> for GitHub, Google and Microsoft.</summary>
public partial class AccountController
{
    /// <summary>
    /// Trades the provider's code for its identity (through <c>/token</c>) and signs that identity in with the cookie.
    /// The callback reaches this only when the code is no Apple code and no step-up or link intent is pending.
    /// </summary>
    private async Task<IActionResult> SignInWithProviderCodeAsync(string code)
    {
        try
        {
            var info = await ExchangeCodeForIdentityAsync(code);
            if (info is null)
            {
                return Redirect("/account/login?error=auth_failed");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, info.Value.ProviderUserId),
                new(ClaimTypes.Name, info.Value.Name),
                new("Name", info.Value.Name),
            };

            // Carry the provider (github/google/microsoft) onto the cookie principal so
            // CurrentUserService can resolve the user by their provider identity, not just the legacy
            // "{name}__{id}" identifier. Absent for legacy cookies and dev login, which fall back to
            // identifier-based resolution.
            if (!string.IsNullOrEmpty(info.Value.Provider))
            {
                claims.Add(new Claim("provider", info.Value.Provider));
            }

            // Stamp WHEN this sign-in happened. CurrentUserService only lets a login that just
            // happened create an account, which is what stops a still-valid cookie for a deleted
            // account from silently minting a new one (see AuthFreshness).
            claims.Add(Services.AuthFreshness.Stamp());

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            // Returning-visitor + remember-method bookkeeping. Both are best-effort UX cookies, so they
            // ride on this successful sign-in and never affect the redirect below.
            SetReturningVisitorCookie();
            TrackProviderUsageAndMaybeRemember(info.Value.Provider);

            var returnUrl = TempData["ReturnUrl"] as string ?? "/walls";
            return LocalRedirect(returnUrl);
        }
        catch (Exception ex)
        {
            Log.Error("[Web Authentication] Authentication callback error: {Message}", ex.Message);
            return Redirect("/account/login?error=callback_failed");
        }
    }
}
