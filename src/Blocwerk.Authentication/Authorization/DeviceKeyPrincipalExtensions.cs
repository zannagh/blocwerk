// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;

namespace Blocwerk.Authentication.Authorization;

/// <summary>The one test the offline client's sync routes apply to an API-key principal.</summary>
public static class DeviceKeyPrincipalExtensions
{
    /// <summary>
    /// True for an API-key principal that is NOT a linked device key: a plain personal key, a wall, kiosk or installation
    /// key. The sync routes (documents, assets, manifest, feed, own marks) still refuse those; a device key reads them as
    /// its owner, under the owner's own per-wall grant. False for a cookie or JWT principal and for anonymous callers.
    /// </summary>
    public static bool IsNonDeviceApiKey(this ClaimsPrincipal principal)
    {
        return principal.IsApiKeyPrincipal() && !principal.IsDeviceKey();
    }
}
