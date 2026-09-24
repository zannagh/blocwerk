namespace Blocwerk.Core.Services;

/// <summary>
/// Thrown when a session signed in with an API key attempts an account-security action: minting or
/// revoking API keys, the password, the second factor, the e-mail address, linking or merging
/// accounts, or deleting the account.
/// </summary>
/// <remarks>
/// A <see cref="UserFacingException"/>, so its fixed message is safe to show and existing
/// <c>InvalidOperationException</c> handlers display it rather than a generic failure. Not an
/// <see cref="UnauthorizedAccessException"/>: the user is signed in, and several call sites turn
/// that one into a login redirect.
/// </remarks>
public sealed class ApiKeySessionRestrictedException : UserFacingException
{
    /// <summary>The one message every refusal shows.</summary>
    public const string UserMessage = "Not available in a session signed in with an API key.";

    public ApiKeySessionRestrictedException()
        : base(UserMessage)
    {
    }
}
