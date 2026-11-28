// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Apple;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.AppleSignIn;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// The Sign in with Apple half of <c>/account/callback</c>. Same three branches as the other providers (step-up, link,
/// sign-in), with the identity resolved through the Apple link model: only the verified <c>sub</c> counts, never an
/// e-mail, and a link never merges two accounts.
/// </summary>
public partial class AccountController
{
    /// <summary>The Return URL Apple posts to on this host; it must be registered on the Services ID.</summary>
    private string AppleRedirectUri => $"{BaseUrl}{AppleWebSettings.CallbackPath}";

    private async Task<IActionResult> HandleAppleCallbackAsync(string code, string state, AppleCallbackTicket ticket)
    {
        // The code rides in this GET's URL, so a URL relayed into somebody else's browser must not complete there:
        // only the browser holding the round trip's cookie, for this very state, gets past this line.
        if (!_apple.IsBoundToThisBrowser(ticket, state, Request, Response))
        {
            Log.Warning("[Web Authentication] Apple callback refused: the code was started in another browser.");
            return Redirect("/account/login?error=auth_failed");
        }

        try
        {
            var subject = await _apple.VerifyAsync(code, ticket, AppleRedirectUri, HttpContext.RequestAborted);
            if (TryReadReauthIntent(state, out var reauthUserId))
            {
                return await HandleAppleReauthAsync(subject, reauthUserId);
            }

            // Only a link started for Apple: an abandoned "Link GitHub" must not turn this sign-in into an Apple link.
            if (TryReadLinkIntent(out var linkUserId, out var linkProvider) && linkProvider == AppleWebSettings.Provider)
            {
                return await HandleAppleLinkAsync(subject, linkUserId);
            }

            return await SignInWithAppleAsync(subject, ticket.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("[Web Authentication] Apple callback error: {Message}", ex.Message);
            return Redirect("/account/login?error=callback_failed");
        }
    }

    private async Task<IActionResult> SignInWithAppleAsync(string? subject, string? name)
    {
        if (subject is null)
        {
            return Redirect("/account/login?error=auth_failed");
        }

        var user = await _apple.FindOrCreateUserAsync(subject, name, HttpContext.RequestAborted);
        if (user is null)
        {
            return Redirect("/account/login?error=auth_failed");
        }

        // The exact user id as the "uid" claim, as a password sign-in: the session resolves by id, never through the
        // UserIdentities lookup an Apple ID is not part of.
        await UserCookieSignIn.SignInAsync(HttpContext, user, isPersistent: false);
        TrackProviderUsageAndMaybeRemember(AppleWebSettings.Provider);

        var returnUrl = TempData["ReturnUrl"] as string ?? "/walls";
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/walls");
    }

    private async Task<IActionResult> HandleAppleLinkAsync(string? subject, Guid linkUserId)
    {
        var (currentUser, refusal) = await CheckLinkSessionAsync(linkUserId);
        if (currentUser is null)
        {
            return refusal!;
        }

        if (subject is null)
        {
            return Redirect("/profile?link=error");
        }

        return await _apple.LinkAsync(currentUser.Id, subject, HttpContext.RequestAborted) switch
        {
            AppleLinkOutcome.Linked => Redirect("/profile?link=linked"),
            AppleLinkOutcome.LinkedElsewhere => Redirect("/profile?link=apple_elsewhere"),
            AppleLinkOutcome.AlreadyLinked => Redirect("/profile?link=apple_already"),
            _ => Redirect("/profile?link=error"),
        };
    }

    private async Task<IActionResult> HandleAppleReauthAsync(string? subject, Guid reauthUserId)
    {
        var (currentUser, refusal) = await CheckReauthSessionAsync(reauthUserId);
        if (currentUser is null)
        {
            return refusal!;
        }

        if (subject is null)
        {
            return Redirect($"{AccountReauthRoutes.DeleteAccountPath}?reauth_error=failed");
        }

        if (!await _apple.IsLinkedToAsync(currentUser.Id, subject, HttpContext.RequestAborted))
        {
            Log.Warning(
                "[Web Authentication] Step-up refused for user {UserId}: the Apple ID is not theirs.",
                currentUser.Id);
            return Redirect($"{AccountReauthRoutes.DeleteAccountPath}?reauth_error=not_yours");
        }

        return ReauthTicketRedirect(currentUser.Id);
    }
}
