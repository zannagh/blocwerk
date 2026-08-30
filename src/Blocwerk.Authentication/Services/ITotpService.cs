namespace Blocwerk.Authentication.Services;

/// <summary>
/// TOTP (RFC 6238) second-factor primitives: secret generation, authenticator provisioning (otpauth
/// URI + QR), code verification, and DataProtection encryption of the shared secret at rest. Stateless —
/// registered as a singleton. The secret is always stored via <see cref="Protect"/> and only ever
/// verified after <see cref="Unprotect"/>; the raw Base32 is never persisted.
/// </summary>
public interface ITotpService
{
    /// <summary>Generates a fresh, cryptographically random Base32-encoded TOTP secret.</summary>
    string GenerateSecret();

    /// <summary>
    /// Builds the <c>otpauth://totp/Blocwerk:{label}?secret=…&amp;issuer=Blocwerk&amp;digits=6&amp;period=30</c>
    /// provisioning URI an authenticator app scans. The label (typically the login username) is URL-encoded.
    /// </summary>
    string BuildOtpAuthUri(string secret, string accountLabel);

    /// <summary>Renders the provisioning URI as a PNG QR code (raw bytes).</summary>
    byte[] BuildQrPng(string otpAuthUri);

    /// <summary>
    /// Verifies a user-entered 6-digit code against the Base32 <paramref name="secret"/>, allowing a
    /// ±1 time-step window to tolerate clock skew. Returns false for any malformed input.
    /// </summary>
    bool Verify(string secret, string code);

    /// <summary>
    /// As <see cref="Verify(string,string)"/>, but also returns the matched time-step in
    /// <paramref name="matchedStep"/> (0 when the code did not match). The caller persists this step and
    /// rejects any later verify whose matched step is not strictly greater, blocking replay of a still-
    /// valid code inside its ±1 window.
    /// </summary>
    bool Verify(string secret, string code, out long matchedStep);

    /// <summary>Encrypts a Base32 secret for storage (DataProtection, purpose "blocwerk.totp").</summary>
    string Protect(string secret);

    /// <summary>Decrypts a secret previously produced by <see cref="Protect"/>.</summary>
    string Unprotect(string protectedSecret);
}
