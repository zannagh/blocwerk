// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.AppleSignIn;

/// <inheritdoc cref="IAppleSignInService"/>
/// <remarks>
/// Every use of a token first verifies it (<see cref="IAppleIdentityTokenValidator"/>), then uses its nonce, so a token
/// works once and only with a nonce this server issued. The link and unlink save tracked rows, so the feed tells the
/// user's devices (<c>user.profile</c>).
/// </remarks>
public sealed class AppleSignInService(
    IDbContextFactory<BlocwerkDbContext> contexts,
    IAppleIdentityTokenValidator validator,
    AppleNonceStore nonces,
    IApiKeyService apiKeys,
    ILogger<AppleSignInService> logger,
    IKioskContext? kioskContext = null,
    TimeProvider? clock = null) : IAppleSignInService
{
    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <inheritdoc/>
    public Task<string> CreateNonceAsync(CancellationToken ct = default) => nonces.IssueAsync(ct);

    /// <inheritdoc/>
    public async Task<AppleLinkOutcome> LinkAsync(Guid userId, string? identityToken, string? nonce, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || GhostUser.Is(userId))
        {
            throw new UnauthorizedAccessException("Only a signed-in user can link an Apple ID.");
        }

        var subject = await VerifyAsync(identityToken, nonce, ct);
        if (subject is null)
        {
            return AppleLinkOutcome.InvalidToken;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var holder = await db.AppleLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Subject == subject, ct);
        if (holder is not null)
        {
            return holder.UserId == userId ? AppleLinkOutcome.Linked : Refuse(userId);
        }

        var own = await db.AppleLinks.FirstOrDefaultAsync(l => l.UserId == userId, ct);
        if (own is null)
        {
            db.AppleLinks.Add(new AppleLink { UserId = userId, Subject = subject, LinkedAt = Clock.GetUtcNow() });
        }
        else
        {
            own.Subject = subject;
            own.LinkedAt = Clock.GetUtcNow();
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another account linked the same Apple ID a moment earlier; the unique index decided.
            return Refuse(userId);
        }

        logger.LogInformation("Apple ID linked to user {UserId}", userId);
        return AppleLinkOutcome.Linked;
    }

    /// <inheritdoc/>
    public async Task<bool> UnlinkAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var link = await db.AppleLinks.FirstOrDefaultAsync(l => l.UserId == userId, ct);
        if (link is null)
        {
            return false;
        }

        db.AppleLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Apple ID unlinked from user {UserId}", userId);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> IsLinkedAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        return await db.AppleLinks.AnyAsync(l => l.UserId == userId, ct);
    }

    /// <inheritdoc/>
    public async Task<AppleSignInResult> SignInAsync(
        string? identityToken,
        string? nonce,
        string? device,
        CancellationToken ct = default)
    {
        await EnsureNotKioskAsync();
        var deviceName = DeviceLinkRequest.CleanDeviceName(device);
        var subject = deviceName is null ? null : await VerifyAsync(identityToken, nonce, ct);
        if (subject is null)
        {
            return new AppleSignInResult(AppleSignInOutcome.InvalidGrant);
        }

        var userId = await LinkedUserAsync(subject, ct);
        if (userId is null)
        {
            logger.LogInformation("Sign in with Apple refused: no live account is linked to this Apple ID");
            return new AppleSignInResult(AppleSignInOutcome.NotLinked);
        }

        var (key, token) = await apiKeys.CreateDeviceKeyAsync(userId.Value, DeviceLinkService.KeyNamePrefix + deviceName, ct);
        logger.LogInformation("Sign in with Apple: key {ApiKeyId} made for user {UserId}", key.Id, userId);
        return new AppleSignInResult(AppleSignInOutcome.SignedIn, new DeviceLinkExchange(key.Id, token, key.Name));
    }

    /// <summary>
    /// Verifies the token, and spends the nonce whatever the token's verdict, so a nonce is good for one attempt only.
    /// </summary>
    private async Task<string?> VerifyAsync(string? identityToken, string? nonce, CancellationToken ct)
    {
        var identity = await validator.ValidateAsync(identityToken, nonce, ct);
        var used = !string.IsNullOrEmpty(nonce) && nonce.Length <= AppleIdentityTokenValidator.MaxNonceLength
            && await nonces.TryUseAsync(nonce);
        if (identity is null)
        {
            return null;
        }

        if (!used)
        {
            logger.LogWarning("Apple identity token refused: nonce not issued, expired or already used");
            return null;
        }

        return identity.Subject;
    }

    private async Task<Guid?> LinkedUserAsync(string subject, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var userId = await db.AppleLinks.Where(l => l.Subject == subject).Select(l => (Guid?)l.UserId).FirstOrDefaultAsync(ct);
        if (userId is null || GhostUser.Is(userId.Value))
        {
            return null;
        }

        var live = await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId && u.DeletedAt == null, ct);
        return live ? userId : null;
    }

    /// <summary>A kiosk tablet never mints a key that outlives its session, whoever's Apple ID it holds.</summary>
    private async Task EnsureNotKioskAsync()
    {
        if (kioskContext is null)
        {
            return;
        }

        await kioskContext.InitializeAsync();
        if (kioskContext.IsKiosk)
        {
            throw new KioskRestrictedException("A kiosk tablet cannot sign in a device.");
        }
    }

    private AppleLinkOutcome Refuse(Guid userId)
    {
        logger.LogWarning("Apple ID link refused for user {UserId}: it is linked to another account", userId);
        return AppleLinkOutcome.LinkedElsewhere;
    }
}
