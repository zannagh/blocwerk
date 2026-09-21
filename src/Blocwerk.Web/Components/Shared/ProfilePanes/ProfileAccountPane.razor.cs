// <copyright file="ProfileAccountPane.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Components.Shared.ProfilePanes;

/// <summary>
/// State and handlers behind the account pane: e-mail verification, password sign-in, TOTP enrolment
/// and OAuth account linking. Lifted verbatim out of the single-file profile page.
/// </summary>
public partial class ProfileAccountPane
{
    /// <summary>The account being edited — always the signed-in viewer.</summary>
    [Parameter]
    [EditorRequired]
    public User Target { get; set; } = default!;

    /// <summary>Raised after a save so the page reloads the viewer and re-renders every pane.</summary>
    [Parameter]
    public EventCallback OnSelfChanged { get; set; }

    /// <summary>Provider keys already linked to this account.</summary>
    [Parameter]
    public IReadOnlyList<string> LinkedProviders { get; set; } = [];

    /// <summary>Every provider enabled on this installation, in display order.</summary>
    [Parameter]
    public IReadOnlyList<(string Key, string Label)> AllProviders { get; set; } = [];

    /// <summary>Result text for a just-completed link round trip, or null.</summary>
    [Parameter]
    public string? LinkMessage { get; set; }

    private string pwUsername = string.Empty;
    private string pwCurrent = string.Empty;
    private string pwPassword = string.Empty;
    private string pwConfirm = string.Empty;
    private bool savingPassword;
    private string? passwordError;
    private bool passwordSuccess;

    private TotpEnrollment? totpEnrollment;
    private string totpCode = string.Empty;
    private bool totpBusy;
    private string? totpError;

    // Verify-email card: the address input (prefilled with the current email), the code input, a
    // "code has been sent" flag that reveals the code field, a shared busy flag, and error/info text.
    private string emailInput = string.Empty;
    private string emailCode = string.Empty;
    private bool emailCodeSent;
    private bool emailBusy;
    private string? emailError;
    private string? emailInfo;

    // The page hands us a fresh User instance after every reload; prefill the address only then, so a
    // re-render triggered elsewhere never clobbers what is being typed.
    private User? syncedFor;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(syncedFor, Target))
        {
            syncedFor = Target;
            emailInput = Target.Email ?? string.Empty;
        }
    }

    // Case-insensitive so a provider key ("github") still matches a stored identity regardless of casing.
    private bool IsProviderLinked(string providerKey) =>
        LinkedProviders.Any(p => string.Equals(p, providerKey, StringComparison.OrdinalIgnoreCase));

    private void StartLink(string provider)
    {
        // Full navigation (not enhanced): /account/link 302s to a cross-origin OAuth provider, which
        // Blazor's fetch-based enhanced navigation cannot follow.
        Navigation.NavigateTo($"/account/link?provider={Uri.EscapeDataString(provider)}", forceLoad: true);
    }

    private async Task SavePasswordAsync()
    {
        savingPassword = true;
        passwordError = null;
        passwordSuccess = false;
        try
        {
            if (pwPassword.Length < 8)
            {
                passwordError = "Password must be at least 8 characters.";
                return;
            }

            if (pwPassword != pwConfirm)
            {
                passwordError = "The passwords don't match.";
                return;
            }

            // Server-side validation (username format, case-insensitive uniqueness, and — when a password
            // already exists — the current-password step-up check) runs in the service and surfaces here
            // as InvalidOperationException. currentPassword is ignored for a first-time set.
            var currentPassword = Target.HasPassword ? pwCurrent : null;
            await CurrentUserService.SetPasswordAsync(pwUsername, pwPassword, currentPassword);
            CurrentUserService.InvalidateCache();
            await OnSelfChanged.InvokeAsync();

            pwCurrent = string.Empty;
            pwPassword = string.Empty;
            pwConfirm = string.Empty;
            passwordSuccess = true;
        }
        catch (InvalidOperationException ex)
        {
            passwordError = ex.Message;
        }
        catch (Exception)
        {
            passwordError = "Couldn't save your password. Please try again.";
        }
        finally
        {
            savingPassword = false;
        }
    }

    private async Task EnableTotpAsync()
    {
        totpBusy = true;
        totpError = null;
        try
        {
            totpEnrollment = await CurrentUserService.BeginTotpEnrollmentAsync();
            totpCode = string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            totpError = ex.Message;
        }
        catch (Exception)
        {
            totpError = "Couldn't start two-factor setup. Please try again.";
        }
        finally
        {
            totpBusy = false;
        }
    }

    private async Task ConfirmTotpAsync()
    {
        totpBusy = true;
        totpError = null;
        try
        {
            var ok = await CurrentUserService.ConfirmTotpAsync(totpCode);
            if (ok)
            {
                totpEnrollment = null;
                totpCode = string.Empty;
                CurrentUserService.InvalidateCache();
                await OnSelfChanged.InvokeAsync();
            }
            else
            {
                totpError = "That code didn't match. Check your app's time and try again.";
            }
        }
        catch (Exception)
        {
            totpError = "Couldn't verify that code. Please try again.";
        }
        finally
        {
            totpBusy = false;
        }
    }

    private void CancelTotpEnrollment()
    {
        totpEnrollment = null;
        totpCode = string.Empty;
        totpError = null;
    }

    private async Task DisableTotpAsync()
    {
        totpBusy = true;
        totpError = null;
        try
        {
            // Step-up: a current authenticator code is required to disable 2FA.
            var ok = await CurrentUserService.DisableTotpAsync(totpCode);
            if (ok)
            {
                totpEnrollment = null;
                totpCode = string.Empty;
                CurrentUserService.InvalidateCache();
                await OnSelfChanged.InvokeAsync();
            }
            else
            {
                totpError = "That code didn't match. Check your app's time and try again.";
            }
        }
        catch (Exception)
        {
            totpError = "Couldn't disable two-factor. Please try again.";
        }
        finally
        {
            totpBusy = false;
        }
    }
}
