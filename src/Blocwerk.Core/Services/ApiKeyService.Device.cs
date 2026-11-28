// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>The personal key of a linked device (the companion app).</summary>
/// <remarks>
/// Linking the same device again replaces its key (earlier device keys with the same name are revoked), and a user
/// keeps at most <see cref="DeviceLinkPolicy.MaxLiveDeviceKeys"/> live device keys, the oldest revoked first. A device
/// key also stops working after <see cref="DeviceLinkPolicy.DeviceKeyIdleLifetime"/> without use (see FindActiveAsync).
/// </remarks>
public partial class ApiKeyService
{
    public async Task<(ApiKey Key, string Token)> CreateDeviceKeyAsync(
        Guid userId,
        string name,
        CancellationToken ct = default)
    {
        EnsureSelf(userId, userId, "mint");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        await RetireDeviceKeysAsync(db, userId, name.Trim(), ct);

        var key = await PersistAsync(db, ApiKeyScope.User, userId, null, name, null, ct, allowWrite: true, isDevice: true);
        logger.LogInformation("Device API key {ApiKeyId} issued for user {UserId}", key.Key.Id, userId);
        return key;
    }

    /// <summary>
    /// Revokes the user's live device keys of the same name, then the oldest ones beyond
    /// <see cref="DeviceLinkPolicy.MaxLiveDeviceKeys"/> minus one, so the new key keeps the count at the cap.
    /// </summary>
    private async Task RetireDeviceKeysAsync(BlocwerkDbContext db, Guid userId, string name, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var live = (await db.ApiKeys.IgnoreQueryFilters()
                .Where(k => k.UserId == userId && k.IsDevice && k.RevokedAt == null)
                .ToListAsync(ct))
            .Where(k => (k.ExpiresAt is null || k.ExpiresAt > now) && !DeviceLinkPolicy.IsIdleExpired(k, now))
            .OrderBy(k => k.CreatedAt)
            .ToList();
        var retire = live.Where(k => string.Equals(k.Name, name, StringComparison.Ordinal)).ToList();
        var others = live.Except(retire).ToList();
        retire.AddRange(others.Take(Math.Max(0, others.Count - (DeviceLinkPolicy.MaxLiveDeviceKeys - 1))));
        foreach (var key in retire)
        {
            key.RevokedAt = now;
            logger.LogInformation("Device API key {ApiKeyId} of user {UserId} retired by a newer link", key.Id, userId);
        }

        await db.SaveChangesAsync(ct);
    }
}
