using System.Net.Mail;
using System.Security.Cryptography;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// DB-backed <see cref="IEmailVerificationService"/>. Codes are 6-digit, cryptographically random, hashed
/// with <see cref="IPasswordService"/> (PBKDF2) before storage, and expire 10 minutes after issue. The
/// only abuse guard is a per-(email, purpose) rate limit: a minimum interval between sends plus a ceiling
/// over a rolling window.
/// </summary>
public class EmailVerificationService : IEmailVerificationService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinResendInterval = TimeSpan.FromSeconds(60);
    private const int MaxCodesPerWindow = 5;
    private const int MaxVerifyAttempts = 5;

    // A fixed dummy hash, computed once (IPasswordService is a singleton), used only to spend comparable
    // PBKDF2 CPU on the no-live-code path so response time doesn't reveal whether a live code exists.
    private static string? dummyCodeHash;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IPasswordService passwordService;
    private readonly IEmailSender emailSender;
    private readonly ILogger<EmailVerificationService> logger;

    public EmailVerificationService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IPasswordService passwordService,
        IEmailSender emailSender,
        ILogger<EmailVerificationService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.passwordService = passwordService;
        this.emailSender = emailSender;
        this.logger = logger;
    }

    public async Task<IssueResult> IssueCodeAsync(
        EmailVerificationPurpose purpose,
        string email,
        Guid? userId,
        CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (!IsValidEmail(normalized))
        {
            return new IssueResult(EmailVerificationStatus.Invalid);
        }

        // Gate on SMTP before touching the DB: with no way to deliver the code, issuing one is pointless.
        if (!emailSender.IsConfigured)
        {
            return new IssueResult(EmailVerificationStatus.EmailNotConfigured);
        }

        var now = DateTimeOffset.UtcNow;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);

        // Rate limit: reject a resend within MinResendInterval, and cap the number of codes per window.
        var windowStart = now - RateLimitWindow;
        var recent = await dbContext.EmailVerificationCodes
            .Where(c => c.Email == normalized && c.Purpose == purpose && c.CreatedUtc >= windowStart)
            .ToListAsync(ct);

        if (recent.Count >= MaxCodesPerWindow
            || recent.Any(c => c.CreatedUtc > now - MinResendInterval))
        {
            return new IssueResult(EmailVerificationStatus.Throttled);
        }

        // Invalidate any still-live code for this (email, purpose): only the newest code stays valid.
        var live = await dbContext.EmailVerificationCodes
            .Where(c => c.Email == normalized && c.Purpose == purpose && c.ConsumedUtc == null)
            .ToListAsync(ct);
        foreach (var stale in live)
        {
            stale.ConsumedUtc = now;
        }

        var code = GenerateCode();
        var entry = new EmailVerificationCode
        {
            Purpose = purpose,
            Email = normalized,
            CodeHash = passwordService.Hash(code),
            UserId = userId,
            CreatedUtc = now,
            ExpiresUtc = now + CodeLifetime,
            AttemptCount = 0,
        };
        await dbContext.EmailVerificationCodes.AddAsync(entry, ct);

        // Send before persisting so a delivery failure leaves no throttling row behind (the user can
        // retry immediately). The plaintext code lives only in this email, never in a log or the result.
        try
        {
            await SendCodeEmailAsync(purpose, normalized, code, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send a {Purpose} verification code.", purpose);
            throw;
        }

        await dbContext.SaveChangesAsync(ct);
        return new IssueResult(EmailVerificationStatus.Success);
    }

    public async Task<VerifyResult> VerifyCodeAsync(
        EmailVerificationPurpose purpose,
        string email,
        string code,
        CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (!IsValidEmail(normalized) || string.IsNullOrWhiteSpace(code))
        {
            return new VerifyResult(EmailVerificationStatus.Invalid);
        }

        var now = DateTimeOffset.UtcNow;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);

        // The latest unconsumed code for this (email, purpose). Generic "Invalid" when there is none, so a
        // caller can't tell an unknown email apart from a wrong code (matters for password-reset).
        var entry = await dbContext.EmailVerificationCodes
            .Where(c => c.Email == normalized && c.Purpose == purpose && c.ConsumedUtc == null)
            .OrderByDescending(c => c.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        if (entry is null)
        {
            // Timing equalization: a wrong guess against a LIVE code runs PBKDF2, so the no-live-entry path
            // must spend comparable CPU or an attacker could time the response to learn whether a live code
            // exists for this (email, purpose). Hash against a fixed dummy hash and discard the result.
            dummyCodeHash ??= passwordService.Hash("000000");
            passwordService.Verify(dummyCodeHash, code);
            return new VerifyResult(EmailVerificationStatus.Invalid);
        }

        if (entry.ExpiresUtc < now)
        {
            entry.ConsumedUtc = now;
            await dbContext.SaveChangesAsync(ct);
            return new VerifyResult(EmailVerificationStatus.Expired);
        }

        entry.AttemptCount++;
        if (entry.AttemptCount > MaxVerifyAttempts)
        {
            entry.ConsumedUtc = now;
            await dbContext.SaveChangesAsync(ct);
            return new VerifyResult(EmailVerificationStatus.TooManyAttempts);
        }

        if (!passwordService.Verify(entry.CodeHash, code))
        {
            // Persist the incremented attempt so the cap actually bites across tries.
            await dbContext.SaveChangesAsync(ct);
            return new VerifyResult(EmailVerificationStatus.Invalid);
        }

        // NOTE: the read-then-consume here has a non-exploitable double-consume race under concurrent verifies
        // of the same live code; a proper fix needs a concurrency token/migration and is intentionally deferred.
        entry.ConsumedUtc = now;
        await dbContext.SaveChangesAsync(ct);
        return new VerifyResult(EmailVerificationStatus.Success, entry.UserId);
    }

    private async Task SendCodeEmailAsync(
        EmailVerificationPurpose purpose,
        string email,
        string code,
        CancellationToken ct)
    {
        var action = purpose switch
        {
            EmailVerificationPurpose.PasswordReset => "reset your password",
            EmailVerificationPurpose.Signup => "finish signing up",
            _ => "verify your email address",
        };

        var subject = $"Your Blocwerk verification code: {code}";
        var htmlBody =
            $"<p>Use this code to {action}:</p>" +
            $"<p style=\"font-size:28px;font-weight:700;letter-spacing:4px\">{code}</p>" +
            "<p>It expires in 10 minutes. If you didn't request it, you can ignore this email.</p>";
        var textBody =
            $"Use this code to {action}: {code}\n\n" +
            "It expires in 10 minutes. If you didn't request it, you can ignore this email.";

        await emailSender.SendAsync(email, subject, htmlBody, textBody, ct);
    }

    // A 6-digit code drawn uniformly from 000000..999999 with a cryptographic RNG (no modulo bias since
    // 1_000_000 divides the sampled range evenly).
    private static string GenerateCode()
    {
        var value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }

    private static string NormalizeEmail(string email) => email?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool IsValidEmail(string email) =>
        !string.IsNullOrWhiteSpace(email) && MailAddress.TryCreate(email, out _);
}
