namespace Blocwerk.Authentication.Services;

/// <summary>
/// The strings the OAuth step-up round trip and the page that starts it must agree on.
/// </summary>
public static class AccountReauthRoutes
{
    /// <summary>Where a completed provider re-authentication lands.</summary>
    public const string DeleteAccountPath = "/settings/delete-account";

    /// <summary>The query parameter carrying the issued ticket back to that page.</summary>
    public const string TicketQueryParameter = "reauth";

    /// <summary>
    /// The entry point that starts the round trip. A POST, not a link: a top-level cross-site
    /// navigation must not be able to set a step-up intent on a signed-in victim's browser.
    /// </summary>
    public const string StartPath = "/account/reauth";

    /// <summary>The form field naming the provider to step up against.</summary>
    public const string ProviderField = "provider";
}
