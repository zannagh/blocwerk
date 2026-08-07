namespace Blocwerk.Core.Services;

/// <summary>
/// Symmetric encryption for third-party tokens stored at rest (e.g. the TopLogger API token).
/// Disabled — <see cref="IsConfigured"/> is false — when no encryption key is configured, so the
/// caller can refuse to store a credential rather than fall back to plaintext.
/// </summary>
public interface ITokenProtector
{
    /// <summary>Whether an encryption key is configured. When false, Protect/Unprotect throw.</summary>
    bool IsConfigured { get; }

    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}
