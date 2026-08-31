using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Issues and verifies one-time email verification codes. Purpose-agnostic and DB-backed so the three
/// flows that consume it — verify-email, password-reset and signup — share one stateless implementation
/// across pages. It never mutates the user (e.g. it does not set <c>User.Email</c>); confirming what a
/// verified code means is the caller's job. Codes are stored hashed only and are rate-limited per
/// (email, purpose) as the sole abuse guard (there is no captcha).
/// </summary>
public interface IEmailVerificationService
{
    /// <summary>
    /// Issues a fresh 6-digit code for <paramref name="email"/> (normalized) and emails it. Any prior
    /// unconsumed code for the same (email, purpose) is invalidated first. Returns
    /// <see cref="EmailVerificationStatus.Throttled"/> when codes were requested too rapidly,
    /// <see cref="EmailVerificationStatus.EmailNotConfigured"/> when SMTP is off, and
    /// <see cref="EmailVerificationStatus.Invalid"/> for a malformed address. The code is never returned
    /// or logged. <paramref name="userId"/> is null for signup (no account yet).
    /// </summary>
    Task<IssueResult> IssueCodeAsync(
        EmailVerificationPurpose purpose,
        string email,
        Guid? userId,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies <paramref name="code"/> against the latest live code for (email, purpose). Increments the
    /// attempt count and invalidates the code once its cap is hit. On success marks the code consumed and
    /// returns the associated <c>UserId</c> when one was stored. Failures are deliberately generic
    /// (<see cref="EmailVerificationStatus.Invalid"/>) so callers cannot use them to probe whether an
    /// email belongs to an account.
    /// </summary>
    Task<VerifyResult> VerifyCodeAsync(
        EmailVerificationPurpose purpose,
        string email,
        string code,
        CancellationToken ct = default);
}

/// <summary>The outcome of <see cref="IEmailVerificationService.IssueCodeAsync"/>.</summary>
public record IssueResult(EmailVerificationStatus Status)
{
    public bool Success => Status == EmailVerificationStatus.Success;
}

/// <summary>
/// The outcome of <see cref="IEmailVerificationService.VerifyCodeAsync"/>. <see cref="UserId"/> carries
/// the account the verified code belonged to (null for signup / on failure).
/// </summary>
public record VerifyResult(EmailVerificationStatus Status, Guid? UserId = null)
{
    public bool Success => Status == EmailVerificationStatus.Success;
}
