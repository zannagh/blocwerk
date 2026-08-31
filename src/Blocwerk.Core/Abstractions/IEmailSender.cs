namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Sends transactional email over SMTP. Callers should check <see cref="IsConfigured"/> first;
/// <see cref="SendAsync"/> throws when no SMTP server is configured so misconfiguration is loud.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// True when SMTP is configured well enough to attempt a send (host + from address present).
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends an email. <paramref name="htmlBody"/> is the HTML part; pass
    /// <paramref name="plainTextBody"/> to include a text alternative.
    /// </summary>
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? plainTextBody = null,
        CancellationToken ct = default);
}
