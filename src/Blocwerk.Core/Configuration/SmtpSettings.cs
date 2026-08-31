namespace Blocwerk.Core.Configuration;

/// <summary>
/// Transport security to use when connecting to the SMTP server.
/// </summary>
public enum SmtpSecurity
{
    /// <summary>Connect in the clear, then upgrade to TLS with STARTTLS (typical port 587).</summary>
    StartTls,

    /// <summary>Open the connection directly over TLS/SSL (typical port 465).</summary>
    ForceTls,

    /// <summary>No transport security (plain connection). Only for local relays/testing.</summary>
    Off,
}

/// <summary>
/// Outgoing SMTP mail configuration, exposed Vaultwarden-style via <c>SMTP__*</c> environment
/// variables (or a <c>Blocwerk:Smtp</c> config section). Left empty by default;
/// <see cref="IsConfigured"/> reports whether a send can be attempted, so features can gate on it.
/// </summary>
public class SmtpSettings
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>The envelope/from address mail is sent from.</summary>
    public string? From { get; set; }

    /// <summary>The display name paired with <see cref="From"/>.</summary>
    public string? FromName { get; set; } = "Blocwerk";

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>True when enough is set (host + from address) to attempt sending mail.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}
