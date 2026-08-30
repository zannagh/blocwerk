namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Per-user, persisted brute-force lockout shared by password and TOTP authentication. The counter and
/// lockout deadline live on the user row (not in a cookie), so they survive a cookie re-mint or a fresh
/// login flow: an attacker who knows the password can neither reset nor bypass the cap by clearing
/// client state. Policy: after <c>5</c> consecutive failures the account is locked for <c>15</c> minutes;
/// the window resets once the lockout passes, and any successful password OR TOTP auth resets it too.
/// </summary>
/// <remarks>
/// DoS tradeoff: because the counter is keyed by user, someone who knows a victim's username can lock
/// that victim out for 15 minutes by failing 5 times. That is accepted for this app's scale — the value
/// of stopping an online 6-digit/​password brute force outweighs the nuisance of a targeted lockout.
/// </remarks>
public interface ILoginLockoutService
{
    /// <summary>
    /// True when the user is currently locked out (a lockout deadline exists and is still in the future).
    /// Callers MUST check this before verifying a password or TOTP code and fail generically when locked.
    /// </summary>
    Task<bool> IsLockedAsync(Guid userId);

    /// <summary>
    /// Records one failed authentication attempt. If the previous lockout has already passed, the window
    /// starts fresh first; then the counter is incremented and, once it reaches the threshold, the
    /// lockout deadline is set. No-op when the user does not exist.
    /// </summary>
    Task RegisterFailureAsync(Guid userId);

    /// <summary>
    /// Clears the failure window (counter to 0, lockout deadline to null) after a successful password or
    /// TOTP authentication. No-op when the user does not exist.
    /// </summary>
    Task ResetAsync(Guid userId);
}
