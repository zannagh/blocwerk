// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.DeviceLinks;

/// <summary>The limits around linked devices, in one place: how long a page, a code and an idle device key last.</summary>
public static class DeviceLinkPolicy
{
    /// <summary>Live (unused, unexpired) codes one user may hold at once; more are refused.</summary>
    public const int MaxLiveCodes = 5;

    /// <summary>Live device keys one user may hold; minting another revokes the oldest.</summary>
    public const int MaxLiveDeviceKeys = 5;

    /// <summary>How long after the consent page was opened its Link button still makes a code.</summary>
    public static readonly TimeSpan PageLifetime = TimeSpan.FromMinutes(5);

    /// <summary>A device key unused this long stops working (measured from its last use, or its creation).</summary>
    public static readonly TimeSpan DeviceKeyIdleLifetime = TimeSpan.FromDays(90);

    /// <summary>True for a device key that has not been used within <see cref="DeviceKeyIdleLifetime"/>.</summary>
    public static bool IsIdleExpired(ApiKey key, DateTimeOffset now) =>
        key.IsDevice && (key.LastUsedAt ?? key.CreatedAt) <= now - DeviceKeyIdleLifetime;
}
