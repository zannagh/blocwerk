// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>
/// API-key principals as <see cref="ApiKeyAuthenticationHandler"/> builds them: a linked device key (the companion app), a
/// plain personal key with write access, and a wall key. The owner is whoever the test's current-user substitute returns.
/// </summary>
internal static class DeviceKeyTestPrincipals
{
    /// <summary>A personal key minted by the app's sign-in: User scope, write access, the device claim.</summary>
    public static ClaimsPrincipal Device() => Key(ApiKeyScope.User, device: true);

    /// <summary>A device key principal for the real key <paramref name="keyId"/>.</summary>
    public static ClaimsPrincipal Device(Guid keyId) => Key(ApiKeyScope.User, device: true, keyId);

    /// <summary>A personal key made under Settings → API keys, with write access but no device claim.</summary>
    public static ClaimsPrincipal Plain() => Key(ApiKeyScope.User, device: false);

    /// <summary>A wall key with write access.</summary>
    public static ClaimsPrincipal Wall() => Key(ApiKeyScope.Wall, device: false);

    private static ClaimsPrincipal Key(ApiKeyScope scope, bool device, Guid? keyId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Some Climber"),
            new(ClaimTypes.NameIdentifier, "gh|4711"),
            new(ApiKeyClaimTypes.Scope, scope.ToString()),
            new(ApiKeyClaimTypes.ApiKeyId, (keyId ?? Guid.NewGuid()).ToString()),
            new(ApiKeyClaimTypes.AllowWrite, "true"),
        };
        if (scope == ApiKeyScope.Wall)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.WallId, Guid.NewGuid().ToString()));
        }

        if (device)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.Device, "true"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName, ClaimTypes.Name, ClaimTypes.Role));
    }
}
