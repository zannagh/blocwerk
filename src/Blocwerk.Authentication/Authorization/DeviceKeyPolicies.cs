// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Builds the two policies that admit a linked device key (the companion app): <see cref="BlocwerkPolicies.WallSync"/> and
/// <see cref="BlocwerkPolicies.DeviceApiKey"/>. Public so the privilege-boundary tests can evaluate them directly.
/// </summary>
/// <remarks>
/// A device key is a personal key minted only by the app's sign-in (<c>ApiKey.IsDevice</c>), listed and revocable under
/// Settings → API keys. It may read what the offline client reads, as its owner; a plain personal key, which a person may
/// paste into any script, still may not.
/// </remarks>
public static class DeviceKeyPolicies
{
    /// <summary>The gallery-image policy widened by a linked device key.</summary>
    public static AuthorizationPolicy BuildWallSync(AuthorizationPolicyBuilder policy)
    {
        return policy
            .AddRequirements(new WallGalleryImageRequirement(admitsDeviceKeys: true))
            .Build();
    }

    /// <summary>A personal key marked as a linked device, on the API-key scheme only, so a browser cookie never satisfies it.</summary>
    public static AuthorizationPolicy BuildDeviceApiKey(AuthorizationPolicyBuilder policy)
    {
        return policy
            .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim(ApiKeyClaimTypes.Scope, ApiKeyScope.User.ToString())
            .RequireClaim(ApiKeyClaimTypes.Device, "true")
            .Build();
    }
}
