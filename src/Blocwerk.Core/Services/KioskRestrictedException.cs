namespace Blocwerk.Core.Services;

/// <summary>
/// Thrown when a kiosk session attempts something that is blocked for every kiosk session regardless
/// of the acting user's authority: an account-security change, minting an API key, or reaching a
/// wall other than the tablet's own.
/// </summary>
/// <remarks>
/// Deliberately NOT an <see cref="UnauthorizedAccessException"/>. That one already means "this user
/// may not do this", and several call sites turn it into a login redirect — which would be exactly
/// wrong here, because the user is signed in and the answer is "not from this device".
/// </remarks>
public sealed class KioskRestrictedException : Exception
{
    public KioskRestrictedException(string message)
        : base(message)
    {
    }
}
