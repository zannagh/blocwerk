namespace Blocwerk.Authentication.Services;

/// <summary>
/// Re-proves that whoever is driving the current session still holds the account, immediately before
/// an irreversible action. A live cookie is not enough on its own: it may have been left signed in on
/// a shared machine, or lifted.
/// </summary>
/// <remarks>
/// This service is scoped, so the provider step-up it records lives on the request or circuit that
/// redeemed the ticket and nowhere else. It cannot be carried in a URL, restored from history, or
/// picked up by a second tab.
/// </remarks>
public interface IAccountReauthService
{
    /// <summary>
    /// Which proof the signed-in user must present, so the UI knows which fields — or which provider
    /// buttons — to show.
    /// </summary>
    Task<AccountReauthRequirements> GetRequirementsAsync();

    /// <summary>
    /// Redeems the ticket a completed provider round trip came back with, DESTROYING it, and records
    /// the step-up against this request/circuit for <see cref="AccountReauthService.ProviderReauthWindow"/>. Returns false
    /// when the ticket is missing, already spent, expired or another user's — in which case the caller
    /// must send the user back through their provider.
    /// </summary>
    Task<bool> RedeemProviderReauthAsync(string? reauthTicket);

    /// <summary>
    /// True when this request/circuit redeemed a provider step-up that is still inside
    /// <see cref="AccountReauthService.ProviderReauthWindow"/>. Purely so the page can show that the round trip worked;
    /// <see cref="VerifyAsync"/> re-checks it.
    /// </summary>
    Task<bool> HasProviderReauthAsync();

    /// <summary>
    /// Verifies the presented proof against the signed-in user. Returns true only when every
    /// credential the account actually has was presented and matched; an account with neither a
    /// password nor an authenticator must instead have redeemed a provider step-up on THIS
    /// request/circuit, still inside its window. There is no path that returns true having checked
    /// nothing.
    /// </summary>
    Task<bool> VerifyAsync(string? password, string? totpCode);
}
