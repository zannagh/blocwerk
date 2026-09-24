// <copyright file="ProfileAccountPane.Email.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Components.Shared.ProfilePanes;

/// <summary>
/// E-mail verification for the account pane: issuing a code, checking it, and the inline write that
/// persists the verified address — the one account-security write on this page with no service seam,
/// so its kiosk refusal sits right at the write.
/// </summary>
public partial class ProfileAccountPane
{
    private async Task SendEmailCodeAsync()
    {
        emailBusy = true;
        emailError = null;
        emailInfo = null;
        if (ApiKeySession.IsApiKeySession)
        {
            emailError = ApiKeySessionRestrictedException.UserMessage;
            emailBusy = false;
            return;
        }

        try
        {
            var result = await EmailVerification.IssueCodeAsync(
                EmailVerificationPurpose.VerifyEmail, emailInput, Target.Id);
            switch (result.Status)
            {
                case EmailVerificationStatus.Success:
                    emailCodeSent = true;
                    emailCode = string.Empty;
                    emailInfo = "We sent a 6-digit code to that address. It expires in 10 minutes.";
                    break;
                case EmailVerificationStatus.Throttled:
                    emailError = "You requested a code very recently. Please wait a minute and try again.";
                    break;
                case EmailVerificationStatus.EmailNotConfigured:
                    emailError = "Email sending isn't configured on this server.";
                    break;
                default:
                    emailError = "That doesn't look like a valid email address.";
                    break;
            }
        }
        catch (Exception)
        {
            emailError = "Couldn't send a code. Please try again.";
        }
        finally
        {
            emailBusy = false;
        }
    }

    private async Task VerifyEmailCodeAsync()
    {
        emailBusy = true;
        emailError = null;
        emailInfo = null;
        try
        {
            var result = await EmailVerification.VerifyCodeAsync(
                EmailVerificationPurpose.VerifyEmail, emailInput, emailCode);
            switch (result.Status)
            {
                case EmailVerificationStatus.Success:
                    await SaveVerifiedEmailAsync(emailInput);
                    break;
                case EmailVerificationStatus.Expired:
                    emailError = "That code has expired. Request a new one.";
                    emailCodeSent = false;
                    break;
                case EmailVerificationStatus.TooManyAttempts:
                    emailError = "Too many attempts. Request a new code.";
                    emailCodeSent = false;
                    break;
                default:
                    emailError = "That code didn't match. Check it and try again.";
                    break;
            }
        }
        catch (Exception)
        {
            emailError = "Couldn't verify that code. Please try again.";
        }
        finally
        {
            emailBusy = false;
        }
    }

    // Persists the (already verified) email onto the current user. Done here via the DbContext factory —
    // the verification service is purpose-agnostic and never touches User.Email. A unique-index clash
    // means the address is already verified on another account.
    private async Task SaveVerifiedEmailAsync(string email)
    {
        // The e-mail address is an account-recovery credential, so changing it is blocked for kiosk
        // sessions like the password and the second factor are. Unlike those it has no service seam
        // to guard — the write is right here — so the check is right here too. /profile is also on
        // the kiosk route deny-list, but a route block is not a mutation block.
        if (KioskContext.IsKiosk)
        {
            emailError = "Email can't be changed from a kiosk device.";
            return;
        }

        // Nor from a session signed in with an API key: the address is how the account is recovered.
        if (ApiKeySession.IsApiKeySession)
        {
            emailError = ApiKeySessionRestrictedException.UserMessage;
            return;
        }

        var normalized = email.Trim().ToLowerInvariant();
        try
        {
            await using var dbContext = await DbContextFactory.CreateDbContextAsync();
            var dbUser = await dbContext.Users.FirstAsync(u => u.Id == Target.Id);
            dbUser.Email = normalized;
            dbUser.EmailVerified = true;
            await dbContext.SaveChangesAsync();

            CurrentUserService.InvalidateCache();
            await OnSelfChanged.InvokeAsync();

            emailCodeSent = false;
            emailCode = string.Empty;
            emailInput = normalized;
            emailInfo = "Your email is verified.";
        }
        catch (DbUpdateException)
        {
            emailError = "That email is already in use by another account.";
        }
    }
}
