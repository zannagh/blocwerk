// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.DeviceLinks;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.DeviceKeys;

/// <summary>
/// <see cref="IDeviceKeyValidator"/> over the database. Never cached: each call reads the key's row, so a revocation
/// takes effect on the very next check.
/// </summary>
public sealed class DeviceKeyValidator : IDeviceKeyValidator
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;

    public DeviceKeyValidator(IDbContextFactory<BlocwerkDbContext> dbContextFactory)
    {
        this.dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc/>
    public async Task<bool> IsLiveAsync(Guid apiKeyId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var key = await db.ApiKeys
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == apiKeyId && k.IsDevice && k.RevokedAt == null, ct);
        return key is not null
            && (key.ExpiresAt is null || key.ExpiresAt > now)
            && !DeviceLinkPolicy.IsIdleExpired(key, now);
    }
}
