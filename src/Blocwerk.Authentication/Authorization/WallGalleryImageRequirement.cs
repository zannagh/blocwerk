using Microsoft.AspNetCore.Authorization;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Marker for the browser's gallery byte route: satisfied by a signed-in human or by an anonymous
/// viewer who brought a wall share token, never by an API key. See
/// <see cref="WallGalleryImageHandler"/>.
/// </summary>
public sealed class WallGalleryImageRequirement : IAuthorizationRequirement;
