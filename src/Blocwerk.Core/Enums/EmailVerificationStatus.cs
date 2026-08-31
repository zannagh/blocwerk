namespace Blocwerk.Core.Enums;

/// <summary>
/// The outcome of issuing or verifying an email verification code. A single set shared by both calls so
/// the verify-email, password-reset and signup UIs can branch on one enum.
/// </summary>
public enum EmailVerificationStatus
{
    /// <summary>The code was issued (and emailed), or the supplied code verified.</summary>
    Success = 0,

    /// <summary>Too many codes were requested too quickly for this (email, purpose); try again later.</summary>
    Throttled = 1,

    /// <summary>The email was malformed, or no matching live code verified (kept generic on purpose).</summary>
    Invalid = 2,

    /// <summary>A matching code existed but its 10-minute window had passed.</summary>
    Expired = 3,

    /// <summary>SMTP is not configured on this server, so no code could be sent.</summary>
    EmailNotConfigured = 4,

    /// <summary>The code's attempt cap was reached; it was invalidated and a new one must be requested.</summary>
    TooManyAttempts = 5,
}
