namespace Blocwerk.Core.Enums;

/// <summary>
/// Why an email verification code was issued. The same issue/verify machinery backs all three flows; the
/// purpose scopes a code so one minted for one flow can never be spent on another.
/// </summary>
public enum EmailVerificationPurpose
{
    /// <summary>Confirm an email address on an existing, signed-in account.</summary>
    VerifyEmail = 0,

    /// <summary>Prove control of an account's email before resetting its password.</summary>
    PasswordReset = 1,

    /// <summary>Confirm an email address during signup, before any account exists.</summary>
    Signup = 2,
}
