// <copyright file="WallRefresh.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Pulse;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared.Refresh;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The "Update panels + 3D" page of a wall: the update's header with its steps, one status line while something is
/// being worked on, and the run's current screen (drop zone, sort, check and apply); the timeline is behind
/// "Details". It re-reads on the run's, the capture's and the runner jobs' pulses (<see cref="PulseLoop"/>), with a
/// safety poll while the worker is busy or the 3D capture is still running, and survives reloads: the run lives on the
/// server.
/// </summary>
public partial class WallRefresh : IDisposable
{
    private static readonly TimeSpan SummaryRecheck = TimeSpan.FromMilliseconds(400);

    private WallRefreshView? view;
    private bool loading = true;
    private bool busy;
    private bool confirmDiscard;
    private bool listView;
    private bool detailsOpen;
    private bool focusDiscard;
    private ElementReference discardButton;
    private string? error;
    private string? blocked;
    private PulseLoop? pulses;
    private Guid pulsesWallId;
    private bool closed;
    private Task? beginning;
    private SplatQuality splatQuality = SplatQuality.High;

    [Parameter]
    public Guid WallId { get; set; }

    [Inject]
    private IWallRefreshService Refreshes { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private WallCacheState Cache { get; set; } = default!;

    [Inject]
    private IWallPulse Pulse { get; set; } = default!;

    [Inject]
    private ILogger<WallRefresh> Logger { get; set; } = default!;

    /// <summary>Something of the run is being worked on: only then does the page re-read without a pulse.</summary>
    private bool Working => view is not null && (view.IsWorking || view.Check3DPending || view.SummaryUpdating || view.Capture is { IsRunning: true });

    /// <summary>Starting the wall over is checked on its summary, which is a list: there is nothing to compare card by card.</summary>
    private bool StartsOver => view is { Summary.FullRevision: not null };

    /// <summary>The check step's card deck is exactly one screen: the page then does not scroll.</summary>
    private bool OneScreen => view is { Status: WallRefreshStatus.ReadyToApply } && !listView && !StartsOver && blocked is null;

    private bool CanDiscard => view is { Status: WallRefreshStatus.Uploading or WallRefreshStatus.ReadyToStart or WallRefreshStatus.ReadyToApply };

    private string DiscardQuestion => RefreshFlow.DiscardQuestion(view);

    public void Dispose()
    {
        closed = true;
        pulses?.Dispose();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (KioskContext.IsKiosk)
        {
            blocked = "Panels and 3D are updated from your own device, not from this wall tablet.";
            loading = false;
            return;
        }

        await ReloadAsync();
        loading = false;
        StartPolling();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusDiscard && confirmDiscard)
        {
            focusDiscard = false;
            await RevealDiscardAsync();
        }
    }

    // Focus alone does not scroll a button that is on screen but under the cookie banner; revealing it honours the
    // CSS scroll-margin, which keeps it clear of the tab bar and the bottom banners.
    private async Task RevealDiscardAsync()
    {
        try
        {
            await discardButton.FocusAsync();
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/reveal-section.js");
            await module.InvokeVoidAsync("revealElement", discardButton);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            // Best effort: the question is still on the page, only not scrolled clear.
        }
    }

    private void AskDiscard()
    {
        confirmDiscard = true;
        focusDiscard = true;
    }

    private async Task ReloadAsync()
    {
        try
        {
            view = await Refreshes.GetCurrentAsync(WallId);
        }
        catch (UnauthorizedAccessException)
        {
            blocked = "Only admins of this wall can update its panels and 3D model.";
        }
        catch (KioskRestrictedException)
        {
            blocked = "Panels and 3D are updated from your own device, not from this wall tablet.";
        }
    }

    private async Task<WallRefreshView?> EnsureRefreshAsync()
    {
        if (view is null)
        {
            // Two change events in a row must open one run, not two.
            beginning ??= RunAsync(async () => view = await Refreshes.BeginAsync(WallId));
            await beginning;
            beginning = null;
        }

        return view;
    }

    /// <summary>
    /// Shows the run right after an answer, and again shortly after while its summary is being made: Apply is back
    /// sooner than the next poll would bring it.
    /// </summary>
    private async Task ReloadAfterAnswerAsync()
    {
        await ReloadAsync();
        StateHasChanged();
        for (var i = 0; i < 4 && view is { SummaryUpdating: true } && !closed; i++)
        {
            await Task.Delay(SummaryRecheck);
            await ReloadAsync();
            StateHasChanged();
        }
    }

    private Task SortAsync() => ActAsync(id => Refreshes.SortAsync(id));

    private Task StartAsync(IReadOnlyList<PanelChoice> choices) => ActAsync(id => Refreshes.StartAsync(id, choices, quality: splatQuality));

    private Task StartPanelsOnlyAsync(IReadOnlyList<PanelChoice> choices) =>
        ActAsync(id => Refreshes.StartAsync(id, choices, keepModel: true));

    private Task StartFullRevisionAsync(IReadOnlyList<PanelChoice> choices) =>
        ActAsync(id => Refreshes.StartAsync(id, choices, fullRevision: true, quality: splatQuality));

    private Task StartFullRevisionPanelsOnlyAsync(IReadOnlyList<PanelChoice> choices) =>
        ActAsync(id => Refreshes.StartAsync(id, choices, keepModel: true, fullRevision: true));

    private Task ApplyAsync()
    {
        // The version of the summary on screen: Apply promotes only what this summary describes.
        var confirmed = view?.Summary?.DecisionsVersion;
        return ActAsync(id => Refreshes.ApplyAsync(id, confirmed));
    }

    private async Task DiscardAsync()
    {
        confirmDiscard = false;
        await ActAsync(id => Refreshes.DiscardAsync(id));
    }

    private async Task StartOverAsync()
    {
        await RunAsync(async () => view = await Refreshes.BeginAsync(WallId));
    }

    private async Task ActAsync(Func<Guid, Task> action)
    {
        if (view is null)
        {
            return;
        }

        var id = view.Id;
        await RunAsync(async () =>
        {
            try
            {
                await action(id);
            }
            finally
            {
                // Also after a refusal: e.g. "the summary changed" is only fixed by showing the current one.
                await ReloadAsync();
            }
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        error = null;
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            blocked = "Only admins of this wall can update its panels and 3D model.";
        }
        finally
        {
            busy = false;
        }
    }

    // Once per wall: enhanced navigation keeps this page for another wall's route.
    private void StartPolling()
    {
        if (pulses is not null && pulsesWallId == WallId)
        {
            return;
        }

        pulses?.Dispose();
        pulsesWallId = WallId;
        pulses = PulseLoop.Start(
            Cache, Pulse, WallId, [PulseTopics.Refresh, PulseTopics.Capture, PulseTopics.GpuJob], () => Working, InvokeAsync, RenderReloadAsync, Logger);
    }

    private async Task RenderReloadAsync()
    {
        await ReloadAsync();
        StateHasChanged();
    }
}
