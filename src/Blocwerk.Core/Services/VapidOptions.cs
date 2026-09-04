namespace Blocwerk.Core.Services;

/// <summary>
/// VAPID (RFC 8292) credentials for signing Web Push requests, bound from the <c>Vapid</c>
/// configuration section. These are set ONLY through environment variables at runtime —
/// <c>VAPID__SUBJECT</c> (a <c>mailto:</c> or site URL), <c>VAPID__PUBLICKEY</c> and
/// <c>VAPID__PRIVATEKEY</c> — and are deliberately NOT present in any committed appsettings file.
/// (Environment variables override appsettings here — CoreServices calls
/// <c>AddEnvironmentVariables()</c> last — so the <c>Vapid</c> section is intentionally absent from
/// appsettings, leaving the env keys as the single authoritative source.) Set the three values in
/// the prod <c>.env</c>; generate a keypair with
/// <c>WebPush.VapidHelper.GenerateVapidKeys()</c>. When any value is empty the push feature disables
/// itself and logs a single warning at startup — real keys are never committed to the repo.
/// </summary>
public sealed class VapidOptions
{
    /// <summary>The VAPID subject: a <c>mailto:</c> or site URL identifying the sender.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The VAPID public key (Base64url), also handed to the client as the applicationServerKey.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>The VAPID private key (Base64url). Server-side only; never sent to the client.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>True only when all three values are present, so push can actually be signed and sent.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Subject)
        && !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey);
}
