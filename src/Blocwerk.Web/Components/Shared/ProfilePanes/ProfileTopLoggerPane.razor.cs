// <copyright file="ProfileTopLoggerPane.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services.TopLogger;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.ProfilePanes;

/// <summary>
/// State and handlers behind the TopLogger import pane: connect/sync/disconnect, grade resolution and
/// the per-gym points calibration. Lifted verbatim out of the single-file profile page.
/// </summary>
public partial class ProfileTopLoggerPane
{
    /// <summary>The signed-in viewer whose TopLogger account this pane manages.</summary>
    [Parameter]
    [EditorRequired]
    public User Viewer { get; set; } = default!;

    /// <summary>Whether the viewer has a password — TopLogger tokens are only stored behind one.</summary>
    [Parameter]
    public bool HasPassword { get; set; }

    /// <summary>Raised with a short status message the page shows as a toast.</summary>
    [Parameter]
    public EventCallback<string> OnToast { get; set; }

    /// <summary>Raised when the viewer asks to jump to the account pane to set a password.</summary>
    [Parameter]
    public EventCallback OnGoToAccount { get; set; }

    // Per-bucket grade-picker selections keyed by raw grade, and the per-grade points inputs for the
    // gym calibration form (blank = unset).
    private readonly Dictionary<string, string?> tlGradeSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> tlPointInputs = new(StringComparer.Ordinal);

    private TopLoggerStatus? tlStatus;
    private string tlAccessToken = string.Empty;
    private string tlRefreshToken = string.Empty;

    // Two-phase busy indicator: "Connecting" (fast token save) then "Syncing" (the long data pull),
    // or null when idle. Everything that used to gate on a plain bool checks `tlBusyPhase is not null`.
    private string? tlBusyPhase;
    private string? tlError;
    private bool tlDeleteAscents;
    private bool tlConfirmingDisconnect;

    // Grade-resolution panel: the unmapped-grade buckets (null until the disclosure is first opened)
    // and a save busy flag.
    private IReadOnlyList<TopLoggerUnmappedGrade>? tlUnmapped;
    private bool tlResolveBusy;

    // Gym points→grade calibration panel: the user's gyms (null until the disclosure first opens), the
    // selected gym, the flash bonus input, and a save flag.
    private IReadOnlyList<TopLoggerGymRef>? tlGyms;
    private Guid? tlSelectedGymId;
    private string tlFlashBonusInput = string.Empty;
    private bool tlCalibBusy;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        tlStatus = await TopLogger.GetStatusAsync(Viewer.Id);
    }

    // Text for the busy indicator and busy buttons, derived from the current phase.
    private string TlBusyLabel => tlBusyPhase == "Syncing" ? "Syncing…" : "Connecting…";

    private async Task ConnectTopLoggerAsync()
    {
        var access = tlAccessToken.Trim();
        var refresh = tlRefreshToken.Trim();
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
        {
            tlError = "Paste both the access token and the refresh token.";
            return;
        }

        tlError = null;
        try
        {
            // Phase 1: save the tokens. Render the "Connecting…" state before the (short) call.
            tlBusyPhase = "Connecting";
            await InvokeAsync(StateHasChanged);

            var connect = await TopLogger.ConnectAsync(Viewer.Id, access, refresh, CancellationToken.None);
            if (connect.PasswordRequired)
            {
                // The client-side gate already hides the form without a password; reflect the backend
                // refusal too, in case the password was cleared mid-session.
                tlError = connect.Error ?? "Set a password before connecting TopLogger.";
                tlBusyPhase = null;
                return;
            }

            if (!connect.Success)
            {
                tlError = connect.Error ?? "Couldn't connect to TopLogger. Please try again.";
                tlBusyPhase = null;
                await ReloadTopLoggerStatusAsync();
                return;
            }

            // Phase 2: connected — now pull the logbook as a visibly separate, spinner-backed phase.
            tlBusyPhase = "Syncing";
            await ReloadTopLoggerStatusAsync();      // status now reads connected
            await InvokeAsync(StateHasChanged);      // render the Syncing spinner before the long await

            var sync = await TopLogger.SyncAsync(Viewer.Id, CancellationToken.None);
            tlBusyPhase = null;
            if (sync.NeedsReauth)
            {
                await OnToast.InvokeAsync("Reset TopLogger Tokens — reconnect below.");
            }
            else if (sync.Success)
            {
                tlAccessToken = string.Empty;
                tlRefreshToken = string.Empty;
                await OnToast.InvokeAsync($"Synced — imported {sync.Imported} ascent(s).");
            }
            else
            {
                tlError = sync.Error ?? "Sync failed. Please try again.";
            }

            await ReloadTopLoggerStatusAsync();
        }
        catch (Exception)
        {
            tlError = "Couldn't connect to TopLogger. Please try again.";
            tlBusyPhase = null;
        }
    }

    private async Task SyncTopLoggerAsync()
    {
        tlError = null;
        try
        {
            // Render the Syncing spinner before the long pull (Blazor Server re-renders after the await).
            tlBusyPhase = "Syncing";
            await InvokeAsync(StateHasChanged);

            var result = await TopLogger.SyncAsync(Viewer.Id, CancellationToken.None);
            tlBusyPhase = null;
            await ReloadTopLoggerStatusAsync();
            if (result.NeedsReauth)
            {
                await OnToast.InvokeAsync("Reset TopLogger Tokens — reconnect below.");
            }
            else if (!result.Success)
            {
                tlError = result.Error ?? "Sync failed. Please try again.";
            }
            else
            {
                await OnToast.InvokeAsync($"Synced — {result.Imported} new, {result.Skipped} already imported.");
            }
        }
        catch (Exception)
        {
            tlError = "Sync failed. Please try again.";
            tlBusyPhase = null;
        }
    }

    private async Task DisconnectTopLoggerAsync()
    {
        tlBusyPhase = "Connecting";
        tlError = null;
        try
        {
            await TopLogger.DisconnectAsync(Viewer.Id, tlDeleteAscents, CancellationToken.None);
            tlConfirmingDisconnect = false;
            tlDeleteAscents = false;
            await ReloadTopLoggerStatusAsync();
            await OnToast.InvokeAsync("TopLogger disconnected.");
        }
        catch (Exception)
        {
            tlError = "Couldn't disconnect. Please try again.";
        }
        finally
        {
            tlBusyPhase = null;
        }
    }

    private async Task ReloadTopLoggerStatusAsync()
    {
        tlStatus = await TopLogger.GetStatusAsync(Viewer.Id, CancellationToken.None);
    }
}
