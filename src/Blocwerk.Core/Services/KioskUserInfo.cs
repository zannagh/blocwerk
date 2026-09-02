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
/// <param name="PinLength">
/// How many digits the PIN has, or 0 when there is none (and 0 for a member consenting without one).
/// This is the ONLY thing about the PIN that is allowed onto the tablet: the on-screen numpad needs
/// it to know when an entry is complete, so it posts exactly once per attempt instead of trying at
/// every keystroke and burning the throttle. The digits themselves never leave the server — they are
/// only ever compared against the stored hash by <see cref="IKioskService.VerifyPinAsync"/>.
/// </param>
public record KioskUserInfo(Guid UserId, string Name, bool HasAvatar, bool RequiresPin, int PinLength);
