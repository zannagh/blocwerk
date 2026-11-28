// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>The personal key of a linked device (the companion app).</summary>
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

        var key = await PersistAsync(db, ApiKeyScope.User, userId, null, name, null, ct, allowWrite: true, isDevice: true);
        logger.LogInformation("Device API key {ApiKeyId} issued for user {UserId}", key.Key.Id, userId);
        return key;
    }
}
