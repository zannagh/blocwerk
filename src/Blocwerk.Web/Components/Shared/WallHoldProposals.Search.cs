// <copyright file="WallHoldProposals.Search.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The "Find holds from all photos" run: a background job (<see cref="IHoldSearchService"/>) this page only starts and
/// watches, so it can be left (or the phone put to sleep) and revisited: the status and, once it ended, the proposals
/// are read again when the page comes back.
/// </summary>
public partial class WallHoldProposals : IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    private HoldSearchStatus? search;
    private CancellationTokenSource? polling;

    [Inject]
    private IHoldSearchService Search { get; set; } = default!;

    private bool Searching => search is { Active: true };

    /// <summary>What a running search is doing, in plain words.</summary>
    internal static string SearchText(HoldSearchStatus s) =>
        s.Total > 0
            ? $"Looking at photo {Math.Min(s.Done + 1, s.Total)} of {s.Total}. You can leave this page; the result will be here when you come back."
            : s.State == Core.Jobs.JobStates.Queued
                ? "Getting ready (another search may be running first). You can leave this page."
                : "Getting ready. You can leave this page.";

    /// <summary>What a search that ended came to, in plain words (null while it runs or when there is none).</summary>
    internal static string? SearchOutcome(HoldSearchStatus? s) => s switch
    {
        { State: Core.Jobs.JobStates.Succeeded, Result: { } r } =>
            $"Last search: {r.Photos} photos searched, {r.Proposals} possible new holds ({r.OnPanels} on a wall photo).",
        { State: Core.Jobs.JobStates.Failed or Core.Jobs.JobStates.Cancelled, Error: { } e } => e,
        _ => null,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        polling?.Cancel();
        polling?.Dispose();
        polling = null;
        GC.SuppressFinalize(this);
    }

    private async Task StartSearchAsync()
    {
        await RunAsync(async () =>
        {
            search = await Search.StartAsync(WallId);
            StartPolling();
            return "Search started.";
        });
    }

    /// <summary>Reads the wall's latest search (after the page opened or the wall changed) and watches it if it runs.</summary>
    private async Task LoadSearchAsync()
    {
        polling?.Cancel();
        search = null;
        try
        {
            search = await Search.GetAsync(WallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Could not read the hold search of wall {WallId}", WallId);
        }

        if (Searching)
        {
            StartPolling();
        }
    }

    private void StartPolling()
    {
        polling?.Cancel();
        polling = new CancellationTokenSource();
        _ = PollAsync(WallId, polling.Token);
    }

    private async Task PollAsync(Guid wallId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var running = false;
                await InvokeAsync(async () => running = await RefreshSearchAsync(wallId));
                if (!running)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left or the wall changed.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Watching the hold search of wall {WallId} stopped", wallId);
        }
    }

    /// <summary>One look at the search; true while it still runs. When it ends the proposals are listed again.</summary>
    private async Task<bool> RefreshSearchAsync(Guid wallId)
    {
        if (wallId != WallId)
        {
            return false;
        }

        var wasRunning = Searching;
        try
        {
            search = await Search.GetAsync(wallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Could not read the hold search of wall {WallId}", wallId);
            return false;
        }

        if (wasRunning && !Searching)
        {
            message = null;
            await ReloadAsync();
        }

        StateHasChanged();
        return Searching;
    }
}
