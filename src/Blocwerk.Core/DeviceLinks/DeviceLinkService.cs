// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.DeviceLinks;

/// <inheritdoc cref="IDeviceLinkService"/>
/// <remarks>
/// The key is a personal key with write access, marked as a device key (<see cref="ApiKey.IsDevice"/>), made through
/// <see cref="IApiKeyService"/>, so it is listed under Settings → API keys and revoked there like any other. Write
/// access because the app records wall captures, which need it; the device mark lets a route admit linked devices
/// without admitting every personal key.
/// </remarks>
public sealed class DeviceLinkService(
    IDbContextFactory<BlocwerkDbContext> contexts,
    IApiKeyService apiKeys,
    ILogger<DeviceLinkService> logger,
    IApiKeySessionContext? apiKeySession = null,
    IKioskContext? kioskContext = null,
    TimeProvider? clock = null) : IDeviceLinkService
{
    /// <summary>What every key a device link makes is called, before the device's name.</summary>
    public const string KeyNamePrefix = "iPhone: ";

    /// <summary>How long a code may wait for its exchange.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>How long a row is kept after it expired, then deleted when the next code is made.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <inheritdoc/>
    public async Task<string> CreateCodeAsync(Guid userId, DeviceLinkRequest request, CancellationToken ct = default)
    {
        await EnsureMayLinkAsync(userId);
        var now = Clock.GetUtcNow();
        var code = DeviceLinkPkce.CreateCode();
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        await db.DeviceLinkCodes.Where(c => c.ExpiresAt < now - Retention).ExecuteDeleteAsync(ct);
        var live = await db.DeviceLinkCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null && c.ExpiresAt > now, ct);
        if (live >= DeviceLinkPolicy.MaxLiveCodes)
        {
            throw new DeviceLinkLimitException();
        }

        db.DeviceLinkCodes.Add(new DeviceLinkCode
        {
            UserId = userId,
            CodeHash = DeviceLinkPkce.Hash(code),
            CodeChallenge = request.CodeChallenge,
            DeviceName = request.DeviceName,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Device link code made for user {UserId}", userId);
        return code;
    }

    /// <inheritdoc/>
    public async Task<DeviceLinkExchange?> ExchangeAsync(
        string? code,
        string? verifier,
        string? device,
        CancellationToken ct = default)
    {
        if (!DeviceLinkPkce.IsWellFormedChallenge(code))
        {
            return null;
        }

        var row = await UseAsync(DeviceLinkPkce.Hash(code!));
        if (row is null)
        {
            logger.LogWarning("Device link exchange refused: code unknown, expired or used");
            return null;
        }

        if (!DeviceLinkPkce.Verifies(verifier, row.CodeChallenge)
            || !string.Equals(DeviceLinkRequest.CleanDeviceName(device), row.DeviceName, StringComparison.Ordinal))
        {
            logger.LogWarning("Device link exchange refused for code {CodeId}: verifier or device mismatch", row.Id);
            return null;
        }

        if (!await IsLiveUserAsync(row.UserId, ct))
        {
            logger.LogWarning("Device link exchange refused for code {CodeId}: user {UserId} gone", row.Id, row.UserId);
            return null;
        }

        var name = KeyNamePrefix + row.DeviceName;
        var (key, token) = await apiKeys.CreateDeviceKeyAsync(row.UserId, name, ct);
        logger.LogInformation("Device link: key {ApiKeyId} made for user {UserId}", key.Id, row.UserId);
        return new DeviceLinkExchange(key.Id, token, key.Name);
    }

    /// <summary>
    /// Only a person signed in as themselves links a device: not a session opened with an API key, not a kiosk tablet
    /// (its member session included), not the Ghost user.
    /// </summary>
    private async Task EnsureMayLinkAsync(Guid userId)
    {
        ApiKeySessionGuard.EnsureNotApiKeySession(apiKeySession);
        if (kioskContext is not null)
        {
            await kioskContext.InitializeAsync();
            if (kioskContext.IsKiosk)
            {
                throw new KioskRestrictedException("A kiosk tablet cannot link a device.");
            }
        }

        if (userId == Guid.Empty || userId == GhostUser.Id)
        {
            throw new UnauthorizedAccessException("Only a signed-in user can link a device.");
        }
    }

    /// <summary>
    /// The one write that decides who used the code: the WHERE carries "unused and unexpired", so of two concurrent
    /// exchanges exactly one changes the row. Not cancellable, so a dropped request still leaves the code used.
    /// </summary>
    private async Task<DeviceLinkCode?> UseAsync(string hash)
    {
        var now = Clock.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        db.CurrentUserId = Guid.Empty;
        var won = await db.DeviceLinkCodes
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, (DateTimeOffset?)now), CancellationToken.None);
        if (won != 1)
        {
            return null;
        }

        return await db.DeviceLinkCodes.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == hash, CancellationToken.None);
    }

    private async Task<bool> IsLiveUserAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        return await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId && u.DeletedAt == null, ct);
    }
}
