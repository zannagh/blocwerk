// <copyright file="WallEnhancementCard.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind of the "Enhance this wall" card: loads the current generation's enhancement, starts or continues it,
/// and refreshes every few seconds while it runs (the run lives on the server, not in this circuit).
/// </summary>
public partial class WallEnhancementCard : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    private Guid loadedWallId;
    private bool busy;
    private bool disposed;
    private string? failure;
    private WallEnhancementView? view;
    private Timer? timer;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: the card is never offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IWallEnhancementService Enhancement { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallEnhancementCard> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        disposed = true;
        timer?.Dispose();
    }

    /// <summary>"Done: 412 changes to look over." for a run waiting for its review.</summary>
    /// <param name="v">The view.</param>
    /// <returns>The line.</returns>
    internal static string ReviewLine(WallEnhancementView v) => v.Pending == 0
        ? "Done, and every change is answered. Finalize it to record the enhancement."
        : $"Done: {WallEnhancementKinds.Count(v.Pending, "change", "changes")} to look over.";

    /// <summary>"Photo 120 of 425, about 35 min left" while a long step reports progress, else null.</summary>
    /// <param name="v">The view.</param>
    /// <returns>The line, or null.</returns>
    internal static string? ProgressLine(WallEnhancementView v)
    {
        if (v.StepProgress is not { Total: > 0 } p)
        {
            return null;
        }

        var line = $"Photo {Math.Min(p.Done + 1, p.Total)} of {p.Total}";
        if (v.StepExpectedEnd is { } end && end > DateTimeOffset.UtcNow)
        {
            var minutes = Math.Max(1, (int)Math.Round((end - DateTimeOffset.UtcNow).TotalMinutes));
            line += $", about {WallEnhancementKinds.Count(minutes, "minute", "minutes")} left";
        }

        return line + ".";
    }

    /// <summary>Who finalized it and what the review kept.</summary>
    /// <param name="v">The view.</param>
    /// <returns>The line.</returns>
    internal static string FinalizedLine(WallEnhancementView v)
    {
        var by = v.FinalizedBy is { } name ? $" by {name}" : string.Empty;
        var kept = v.Counts.Sum(c => c.Accepted);
        var undone = v.Counts.Sum(c => c.Rejected);
        return $"Finalized{by}: {WallEnhancementKinds.Count(kept, "change", "changes")} kept or carried out, {undone} undone or turned down. "
               + "It runs again only after the next wall update.";
    }

    // Route data is loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced navigation.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        failure = null;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            view = await Enhancement.GetAsync(WallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException or UserFacingException)
        {
            Logger.LogWarning(ex, "Could not load the enhancement of wall {WallId}", WallId);
            view = null;
        }

        SchedulePoll();
    }

    private void SchedulePoll()
    {
        if (view?.State == WallEnhancementState.Running && !disposed)
        {
            timer ??= new Timer(_ => _ = InvokeAsync(PollAsync), null, Poll, Poll);
            return;
        }

        timer?.Dispose();
        timer = null;
    }

    private async Task PollAsync()
    {
        if (disposed)
        {
            return;
        }

        await ReloadAsync();
        StateHasChanged();
    }

    private async Task CancelAsync()
    {
        if (busy)
        {
            return;
        }

        busy = true;
        failure = null;
        try
        {
            view = await Enhancement.CancelAsync(WallId);
            SchedulePoll();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException or UserFacingException)
        {
            failure = ex.Message;
            await ReloadAsync();
        }
        finally
        {
            busy = false;
        }
    }

    private async Task StartAsync()
    {
        // A double tap lands before the disabled button reaches the phone: refuse the second start.
        if (busy)
        {
            return;
        }

        busy = true;
        failure = null;
        try
        {
            view = await Enhancement.StartAsync(WallId);
            SchedulePoll();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException or UserFacingException)
        {
            Logger.LogWarning(ex, "Could not start the enhancement of wall {WallId}", WallId);
            failure = ex.Message;
            await ReloadAsync();
        }
        finally
        {
            busy = false;
        }
    }
}
