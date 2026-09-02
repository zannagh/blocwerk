using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Authentication.Providers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace Blocwerk.Web.State;

/// <summary>
/// Logs the ascent a kiosk visitor tapped BEFORE anybody was picked, once the pick has happened.
/// Extracted from KioskController so the controller stays about identity transitions.
/// </summary>
public sealed class KioskPendingAttemptLogger
{
    /// <summary>
    /// How coarsely the deterministic idempotency key buckets time. A double submit (a
    /// double-tapped button, a re-POST after a back navigation) lands in the same bucket and is
    /// therefore the same attempt as far as AttemptService is concerned; a genuine re-tap minutes
    /// later gets a fresh key. Bucket boundaries can split a double submit, in which case the
    /// service's own debounce is still there as the second line — the point of the key is that
    /// idempotency is designed in rather than left entirely to a timing window.
    /// </summary>
    private static readonly TimeSpan IdempotencyBucket = TimeSpan.FromMinutes(5);

    private readonly IBoulderService boulderService;
    private readonly IAttemptService attemptService;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly ILogger<KioskPendingAttemptLogger> logger;

    public KioskPendingAttemptLogger(
        IBoulderService boulderService,
        IAttemptService attemptService,
        AuthenticationStateProvider authenticationStateProvider,
        ILogger<KioskPendingAttemptLogger> logger)
    {
        this.boulderService = boulderService;
        this.attemptService = attemptService;
        this.authenticationStateProvider = authenticationStateProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Logs the pending ascent and returns the boulder page to land on, or <c>null</c> when there
    /// is nothing pending or the pending action does not survive its checks and the caller should
    /// fall back to the normal wall redirect.
    /// </summary>
    /// <param name="httpContext">The request the sign-in just happened on.</param>
    /// <param name="principal">The principal that was signed in on this request.</param>
    /// <param name="wallId">The wall from the DEVICE COOKIE — the only one that may be logged against.</param>
    /// <param name="signedInUserId">The member who actually signed in.</param>
    /// <param name="pending">The caller-supplied pending action.</param>
    public async Task<string?> TryLogAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        Guid wallId,
        Guid signedInUserId,
        KioskPendingAction pending)
    {
        if (pending.BoulderId is not { } boulderId || pending.SafeType is not { } typeText)
        {
            return null;
        }

        // The pending action is bound to the member it was picked FOR. Without that binding it
        // would ride the URL and be applied to whoever eventually got through the PIN step — so a
        // climber who mistyped and walked off would have their ascent credited to the next person
        // to use the tablet. An absent id is treated the same way as a wrong one: there is no
        // honest "whoever signed in" fallback.
        if (pending.UserId is not { } intendedUserId)
        {
            logger.LogWarning(
                "Kiosk pending ascent dropped: no intended user was carried for boulder {BoulderId}",
                boulderId);
            return null;
        }

        if (intendedUserId != signedInUserId)
        {
            logger.LogWarning(
                "Kiosk pending ascent dropped: it was picked for user {IntendedUserId} but {SignedInUserId} signed in",
                intendedUserId,
                signedInUserId);
            return null;
        }

        // TryParse alone would accept any number as an AttemptType; IsDefined is what makes it a
        // real member of the enum.
        if (!Enum.TryParse<AttemptType>(typeText, ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            logger.LogWarning(
                "Kiosk pending ascent dropped: {PendingType} is not an attempt type", typeText);
            return null;
        }

        // SignInAsync only wrote a Set-Cookie header — HttpContext.User is still anonymous, and the
        // authentication state provider would resolve from that same unauthenticated request. Both
        // have to be corrected before anything resolves the acting user, or the attempt lands on
        // the wrong identity (or on none at all).
        httpContext.User = principal;
        if (authenticationStateProvider is CookieAuthenticationStateProvider cookieStateProvider)
        {
            cookieStateProvider.AdoptSignedInPrincipal(principal);
        }

        // Everything below now runs AS the picked member. GetBoulderAsync puts the read under the
        // wall membership filter and the kiosk wall gate; the explicit wall comparison is the second
        // lock, because the wall that may be logged against is the one in the device cookie and
        // nothing else.
        var boulder = await boulderService.GetBoulderAsync(boulderId);
        if (boulder is null || boulder.WallId != wallId)
        {
            logger.LogWarning(
                "Kiosk pending ascent dropped: boulder {BoulderId} is not readable on wall {WallId}",
                boulderId,
                wallId);
            return null;
        }

        if (boulder.IsDraft)
        {
            // AttemptService refuses drafts anyway; catching it here keeps the redirect honest.
            logger.LogWarning(
                "Kiosk pending ascent dropped: boulder {BoulderId} is still a draft", boulderId);
            return null;
        }

        try
        {
            await attemptService.LogAttemptAsync(
                boulderId,
                type,
                clientRequestId: BuildClientRequestId(boulderId, signedInUserId, type, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            // The sign-in itself succeeded, so still land on the boulder: the climber is now picked
            // and can simply tap again, which is a far better outcome than an error page.
            logger.LogWarning(
                ex, "Kiosk pending ascent on boulder {BoulderId} could not be logged", boulderId);
        }

        return $"/walls/{wallId}/boulders/{boulderId}";
    }

    /// <summary>
    /// A stable idempotency key for one tap: the same boulder, member, type and time bucket always
    /// produce the same GUID, so a replayed POST returns the attempt already stored instead of
    /// logging a second one. Derived rather than random precisely because a re-POST is a NEW
    /// request that could not carry a random id forward.
    /// </summary>
    private static Guid BuildClientRequestId(Guid boulderId, Guid userId, AttemptType type, DateTimeOffset now)
    {
        var bucket = now.UtcTicks / IdempotencyBucket.Ticks;
        var seed = $"kiosk-pending|{boulderId}|{userId}|{type}|{bucket}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash.AsSpan(0, 16));
    }
}
