using Microsoft.AspNetCore.Authorization;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Marker for the browser's gallery byte route: satisfied by a signed-in human or by an anonymous
/// viewer who brought a wall share token, never by an API key. See
/// <see cref="WallGalleryImageHandler"/>. The sync routes' variant (<see cref="BlocwerkPolicies.WallSync"/>) also admits a
/// linked device key.
/// </summary>
/// <param name="admitsDeviceKeys">True to let a linked device key through as well.</param>
public sealed class WallGalleryImageRequirement(bool admitsDeviceKeys = false) : IAuthorizationRequirement
{
    /// <summary>True when a linked device key satisfies the requirement (the sync routes); every other key never does.</summary>
    public bool AdmitsDeviceKeys { get; } = admitsDeviceKeys;
}
