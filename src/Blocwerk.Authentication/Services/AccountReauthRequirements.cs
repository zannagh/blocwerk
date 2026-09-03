namespace Blocwerk.Authentication.Services;

/// <summary>What the signed-in user must present to re-prove control of their account.</summary>
public sealed class AccountReauthRequirements
{
    /// <summary>True when the account has a password login that must be typed again.</summary>
    public bool RequiresPassword { get; init; }

    /// <summary>True when the account has a confirmed authenticator that must produce a code.</summary>
    public bool RequiresTotp { get; init; }

    /// <summary>
    /// True when the account holds no local credential at all (the OAuth-only majority) and must
    /// therefore prove itself by signing in with its provider again, right now.
    /// </summary>
    /// <remarks>
    /// Without this such an account had nothing to re-prove: the irreversible deletion sat behind the
    /// session cookie alone, and the confirmation phrase the page asks for is the user's own e-mail
    /// address, printed on that same page.
    /// </remarks>
    public bool RequiresProviderReauth { get; init; }

    /// <summary>
    /// The providers that can carry out <see cref="RequiresProviderReauth"/> — the ones this
    /// installation has configured. Empty when the operator has enabled none.
    /// </summary>
    public IReadOnlyList<string> ProviderOptions { get; init; } = [];

    /// <summary>
    /// False when the account can present no step-up whatsoever: no password, no authenticator, and
    /// no configured provider to sign in with again. Deletion has to refuse rather than fall back to
    /// the cookie, so the UI says to set a password first.
    /// </summary>
    public bool CanStepUp =>
        RequiresPassword || RequiresTotp || (RequiresProviderReauth && ProviderOptions.Count > 0);
}
