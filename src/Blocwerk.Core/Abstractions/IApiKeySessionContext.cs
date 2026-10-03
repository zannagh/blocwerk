namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Whether the current request or circuit is a browser session that was signed in with a personal
/// API key (<c>POST /account/api-key-login</c>) rather than by the person themselves.
/// </summary>
/// <remarks>
/// Lives in Core, like <see cref="IKioskContext"/>, so Core services can refuse account-security
/// actions without depending on the auth stack; the implementation reads the session's claims in
/// Blocwerk.Authentication. Use it only to RESTRICT: over-restricting is safe.
/// </remarks>
public interface IApiKeySessionContext
{
    /// <summary>True when this session was signed in with an API key.</summary>
    bool IsApiKeySession { get; }
}
