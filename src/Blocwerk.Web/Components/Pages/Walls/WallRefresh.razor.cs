// <copyright file="WallRefresh.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared.Refresh;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The "Update panels + 3D" page of a wall: the update's header with its steps, one status line while something is
/// being worked on, and the run's current screen (drop zone, sort, check and apply); the timeline is behind
/// "Details". It polls while the worker is busy or the 3D capture is still running, and survives reloads: the run
/// lives on the server.
/// </summary>
public partial class WallRefresh : IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
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
    private PeriodicTimer? timer;
    private CancellationTokenSource? polling;
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

    /// <summary>Starting the wall over is checked on its summary, which is a list: there is nothing to compare card by card.</summary>
    private bool StartsOver => view is { Summary.FullRevision: not null };

    /// <summary>The check step's card deck is exactly one screen: the page then does not scroll.</summary>
    private bool OneScreen => view is { Status: WallRefreshStatus.ReadyToApply } && !listView && !StartsOver && blocked is null;

    private bool CanDiscard => view is { Status: WallRefreshStatus.Uploading or WallRefreshStatus.ReadyToStart or WallRefreshStatus.ReadyToApply };

    private string DiscardQuestion => RefreshFlow.DiscardQuestion(view);

    public void Dispose()
    {
        polling?.Cancel();
        polling?.Dispose();
        timer?.Dispose();
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
        for (var i = 0; i < 4 && view is { SummaryUpdating: true } && polling is { IsCancellationRequested: false }; i++)
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

    private void StartPolling()
    {
        if (timer is not null)
        {
            return;
        }

        timer = new PeriodicTimer(PollEvery);
        polling = new CancellationTokenSource();
        _ = PollAsync(timer, polling.Token);
    }

    private async Task PollAsync(PeriodicTimer ticks, CancellationToken ct)
    {
        try
        {
            while (await ticks.WaitForNextTickAsync(ct))
            {
                if (view is null || !(view.IsWorking || view.Check3DPending || view.SummaryUpdating || view.Capture is { IsRunning: true }))
                {
                    continue;
                }

                try
                {
                    await InvokeAsync(async () =>
                    {
                        await ReloadAsync();
                        StateHasChanged();
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missed poll is retried on the next tick.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
    }
}
