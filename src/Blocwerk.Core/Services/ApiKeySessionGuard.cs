using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Services;

/// <summary>
/// The one refusal for account-security actions in a session signed in with an API key.
/// </summary>
/// <remarks>
/// A key login exists so automation can drive the app; it must not be able to turn a leaked key into
/// a permanent takeover (a new password, a second factor the owner does not hold, a changed recovery
/// e-mail, another linked login, fresh keys) or erase the account. A null context — a host that
/// never registered one — answers "not a key session", exactly as <see cref="KioskGuard"/> treats a
/// missing kiosk context; the web host always registers it.
/// </remarks>
public static class ApiKeySessionGuard
{
    /// <summary>Throws <see cref="ApiKeySessionRestrictedException"/> in an API-key session.</summary>
    public static void EnsureNotApiKeySession(IApiKeySessionContext? context)
    {
        if (context is { IsApiKeySession: true })
        {
            throw new ApiKeySessionRestrictedException();
        }
    }
}
