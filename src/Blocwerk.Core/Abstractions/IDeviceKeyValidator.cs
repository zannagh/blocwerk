// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Re-checks a linked device key against the database: it still exists, is a device key, and is not revoked, expired
/// or idle-expired. The authentication handler checks this once per request; a long request (the feed stream) asks again
/// so a revoked key stops reading while the connection is still open.
/// </summary>
public interface IDeviceKeyValidator
{
    /// <summary>True while this device key may still read.</summary>
    Task<bool> IsLiveAsync(Guid apiKeyId, CancellationToken ct = default);
}
