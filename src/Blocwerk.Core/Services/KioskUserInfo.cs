namespace Blocwerk.Core.Services;

/// <summary>
/// One entry of a kiosk's user picker. Deliberately minimal: the tablet is unauthenticated while the
/// picker is on screen, so nothing beyond a name, whether an avatar exists, and whether a PIN is
/// needed may leave the server.
/// </summary>
/// <param name="UserId">The picked user's id.</param>
/// <param name="Name">The user's effective display name.</param>
/// <param name="HasAvatar">True when the user has an avatar image to render.</param>
/// <param name="RequiresPin">True when picking this user requires typing their kiosk PIN.</param>
public record KioskUserInfo(Guid UserId, string Name, bool HasAvatar, bool RequiresPin);
