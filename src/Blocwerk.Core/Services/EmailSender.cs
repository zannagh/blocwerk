using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IEmailSender"/> backed by MailKit. Stateless — a fresh <see cref="SmtpClient"/> is
/// created, connected, (optionally) authenticated and disconnected per send, so the sender is safe
/// to register as a singleton.
/// </summary>
public class EmailSender : IEmailSender
{
    private readonly SmtpSettings settings;
    private readonly ILogger<EmailSender> logger;

    public EmailSender(BlocwerkSettings settings, ILogger<EmailSender> logger)
    {
        this.settings = settings.Smtp;
        this.logger = logger;
    }

    public bool IsConfigured => settings.IsConfigured;

    public async Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? plainTextBody = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "SMTP is not configured (SMTP__HOST / SMTP__FROM are empty). Check IsConfigured before sending.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? string.Empty, settings.From));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;

        var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
        if (!string.IsNullOrWhiteSpace(plainTextBody))
        {
            bodyBuilder.TextBody = plainTextBody;
        }

        message.Body = bodyBuilder.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, MapSecurity(settings.Security), ct);

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email to {Recipient} with subject {Subject}.", toEmail, subject);
            throw;
        }
    }

    private static SecureSocketOptions MapSecurity(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.ForceTls => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.Off => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}
