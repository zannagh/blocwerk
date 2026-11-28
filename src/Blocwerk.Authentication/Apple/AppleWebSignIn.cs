// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Apple;

/// <summary>
/// Sign in with Apple for the browser, in the shape of the other OAuth providers: an authorize redirect, a callback,
/// and a verified Apple <c>sub</c> at the end.
/// </summary>
/// <remarks>
/// The round trip: <see cref="BeginAuthorize"/> seals the state and a fresh nonce into the browser
/// (<see cref="AppleOAuthCookie"/>) and returns the authorize query; Apple posts the code back to
/// <see cref="AppleWebSettings.CallbackPath"/>, where <see cref="AcceptCallback"/> checks the state against that
/// cookie and parks the code; <c>/account/callback</c> then takes it (<see cref="TryTakeTicket"/>), trades it at Apple
/// and verifies the identity token with the shared validator, for the web audiences only
/// (<c>AppleSignIn:WebAudiences</c>, the Services ID): a token issued to the iOS app is refused here.
/// </remarks>
public sealed class AppleWebSignIn(
    AppleWebSettings settings,
    AppleClientSecret clientSecret,
    AppleOAuthCookie cookie,
    AppleCallbackStore callbacks,
    AppleTokenClient tokens,
    IAppleIdentityTokenValidator validator,
    AppleSignInSettings tokenSettings,
    AppleWebAccounts accounts,
    IAppleSignInService links)
{
    /// <summary>Gets a value indicating whether the four settings are present and the key loads.</summary>
    public bool IsEnabled => settings.IsConfigured && clientSecret.HasKey;

    /// <summary>Gets Apple's authorize endpoint.</summary>
    public string AuthorizeUrl => settings.AuthorizeUrl;

    /// <summary>Gets the Services ID, the web client id.</summary>
    public string ServicesId => settings.ServicesId;

    /// <summary>
    /// Starts a round trip for <paramref name="state"/>: writes the binding cookie and returns the Apple-specific
    /// authorize parameters (besides client id, state and redirect URI).
    /// </summary>
    public IReadOnlyDictionary<string, string> BeginAuthorize(HttpResponse response, string state)
    {
        var rawNonce = DeviceLinkPkce.CreateCode();
        cookie.Write(response, state, rawNonce);
        return new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["response_mode"] = "form_post",
            ["scope"] = "name",
            ["nonce"] = DeviceLinkPkce.Hash(rawNonce),
        };
    }

    /// <summary>
    /// Accepts Apple's form POST: true and the code parked when the browser's cookie seals exactly
    /// <paramref name="state"/>. The cookie stays for <see cref="IsBoundToThisBrowser"/>; a refused or cancelled
    /// callback (no code) drops it.
    /// </summary>
    public bool AcceptCallback(HttpRequest request, HttpResponse response, string state, string? code, string? userField)
    {
        var rawNonce = cookie.Peek(request, state);
        if (rawNonce is null || string.IsNullOrEmpty(code) || !callbacks.Add(code, state, rawNonce, AppleUserName.Parse(userField)))
        {
            cookie.Delete(response);
            return false;
        }

        return true;
    }

    /// <summary>Takes the parked ticket of an Apple code; false for any code Apple's callback did not park.</summary>
    public bool TryTakeTicket(string? code, [NotNullWhen(true)] out AppleCallbackTicket? ticket) =>
        callbacks.TryTake(code, out ticket);

    /// <summary>
    /// True when the browser completing the round trip is the one that started it: the callback's
    /// <paramref name="state"/> is the ticket's, and this browser's cookie seals that state and the ticket's nonce.
    /// Spends the cookie. Without this a <c>/signing-in?state&amp;code</c> URL relayed into another browser would sign
    /// that browser in, or link to its account, with somebody else's Apple ID.
    /// </summary>
    public bool IsBoundToThisBrowser(AppleCallbackTicket ticket, string? state, HttpRequest request, HttpResponse response)
    {
        var rawNonce = cookie.Take(request, response, ticket.State);
        return rawNonce is not null
            && string.Equals(state, ticket.State, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(rawNonce), Encoding.UTF8.GetBytes(ticket.RawNonce));
    }

    /// <summary>The verified Apple <c>sub</c> for <paramref name="code"/>, or null.</summary>
    public async Task<string?> VerifyAsync(string code, AppleCallbackTicket ticket, string redirectUri, CancellationToken ct = default)
    {
        var idToken = await tokens.ExchangeAsync(code, redirectUri, ct);
        if (idToken is null)
        {
            return null;
        }

        var identity = await validator.ValidateAsync(idToken, ticket.RawNonce, tokenSettings.WebAudiences, ct);
        return identity?.Subject;
    }

    /// <inheritdoc cref="AppleWebAccounts.FindOrCreateAsync"/>
    public Task<User?> FindOrCreateUserAsync(string subject, string? name, CancellationToken ct = default) =>
        accounts.FindOrCreateAsync(subject, name, ct);

    /// <inheritdoc cref="AppleWebAccounts.IsLinkedToAsync"/>
    public Task<bool> IsLinkedToAsync(Guid userId, string subject, CancellationToken ct = default) =>
        accounts.IsLinkedToAsync(userId, subject, ct);

    /// <summary>
    /// Links the verified <paramref name="subject"/> to <paramref name="userId"/> through the shared link model: it
    /// replaces an Apple ID linked before, and is refused when another account holds this one. Never merges.
    /// </summary>
    public Task<AppleLinkOutcome> LinkAsync(Guid userId, string subject, CancellationToken ct = default) =>
        links.LinkSubjectAsync(userId, subject, ct);
}
