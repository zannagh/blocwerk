namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Hashes and verifies login passwords with a salted key-derivation function. Plaintext is never
/// stored; each call to <see cref="Hash"/> produces a fresh per-password random salt.
/// </summary>
public interface IPasswordService
{
    /// <summary>
    /// Produces a salted KDF hash of <paramref name="password"/> suitable for persisting in
    /// <see cref="Entities.User.PasswordHash"/>.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Returns true when <paramref name="password"/> matches the stored <paramref name="hash"/>.
    /// A "needs rehash" outcome still counts as a successful verification.
    /// </summary>
    bool Verify(string hash, string password);
}
