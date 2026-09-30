// <copyright file="WallRefresh.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The "Update panels + 3D" page of a wall: shows the run's current screen (drop zone, sort, confirm) and its
/// timeline, polling while the worker is busy or the 3D capture is still running. It survives reloads: the run
/// lives on the server.
/// </summary>
public partial class WallRefresh : IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    private WallRefreshView? view;
    private bool loading = true;
    private bool busy;
    private bool confirmDiscard;
    private bool focusDiscard;
    private ElementReference discardButton;
    private string? error;
    private string? blocked;
    private PeriodicTimer? timer;
    private CancellationTokenSource? polling;
    private Task? beginning;

    [Parameter]
    public Guid WallId { get; set; }

    [Inject]
    private IWallRefreshService Refreshes { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    private bool CanDiscard => view is { Status: WallRefreshStatus.Uploading or WallRefreshStatus.ReadyToStart or WallRefreshStatus.ReadyToApply };

    private string DiscardQuestion
    {
        get
        {
            if (view is { Status: WallRefreshStatus.ReadyToApply })
            {
                return "Discard this panel update? The panels keep their current photos and holds.";
            }

            var photos = view?.Photos.Count ?? 0;
            var videos = view?.Videos.Count ?? 0;
            var parts = new List<string>();
            if (photos > 0)
            {
                parts.Add(photos == 1 ? "1 photo" : $"{photos} photos");
            }

            if (videos > 0)
            {
                parts.Add(videos == 1 ? "1 video" : $"{videos} videos");
            }

            return parts.Count == 0
                ? "Discard this update?"
                : $"Discard {string.Join(" and ", parts)}? They are deleted; the wall stays as it is.";
        }
    }

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
            // Focusing scrolls the question into view, clear of the tab bar and bottom banners (scroll-margin in the CSS).
            focusDiscard = false;
            await discardButton.FocusAsync();
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

    private async Task<Guid?> EnsureRefreshAsync()
    {
        if (view is null)
        {
            // Two change events in a row must open one run, not two.
            beginning ??= RunAsync(async () => view = await Refreshes.BeginAsync(WallId));
            await beginning;
            beginning = null;
        }

        return view?.Id;
    }

    private Task SortAsync() => ActAsync(id => Refreshes.SortAsync(id));

    private Task StartAsync(IReadOnlyList<PanelChoice> choices) => ActAsync(id => Refreshes.StartAsync(id, choices));

    private Task ApplyAsync() => ActAsync(id => Refreshes.ApplyAsync(id));

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
            await action(id);
            await ReloadAsync();
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
                if (view is null || !(view.IsWorking || view.Check3DPending || view.Capture is { IsRunning: true }))
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
