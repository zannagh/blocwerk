namespace Blocwerk.Authentication.Resources;

public struct RedirectSettings
{
    public required string Uri { get; set; }

    public required string Provider { get; set; }

    /// <summary>
    /// The "remember my sign-in method" choice carried across the OAuth round-trip: true to store the
    /// preference, false to clear it, null to leave it untouched. Applied only once login succeeds.
    /// </summary>
    public bool? Remember { get; set; }
}
