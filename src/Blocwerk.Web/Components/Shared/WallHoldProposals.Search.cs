// <copyright file="WallHoldProposals.Search.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Pulse;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The "Find holds from all photos" run: a background job (<see cref="IHoldSearchService"/>) this page only starts and
/// watches on its pulses (<see cref="PulseTopics.HoldSearch"/>, with a safety poll while it runs), so it can be left (or
/// the phone put to sleep) and revisited: the status and, once it ended, the proposals are read again when the page comes
/// back.
/// </summary>
public partial class WallHoldProposals : IDisposable
{
    private readonly PulseHolder<PulseLoop> searchPulses = new();
    private HoldSearchStatus? search;

    [Inject]
    private IHoldSearchService Search { get; set; } = default!;

    [Inject]
    private WallCacheState Cache { get; set; } = default!;

    [Inject]
    private IWallPulse Pulse { get; set; } = default!;

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
        searchPulses.Dispose();
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
        var wallId = WallId;
        search = null;
        HoldSearchStatus? read = null;
        try
        {
            read = await Search.GetAsync(wallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Could not read the hold search of wall {WallId}", wallId);
        }

        if (wallId != WallId)
        {
            return; // the wall changed while this read ran: the newer load shows and watches its own
        }

        search = read;
        StartPolling();
    }

    // One loop per wall; it idles (idle poll) between searches, so one started on another device shows up too.
    private void StartPolling()
    {
        // Called after awaits (LoadSearchAsync, a start): once the page is disposed, the holder opens nothing.
        if (searchPulses.IsFor(WallId))
        {
            searchPulses.Current?.Nudge();
            return;
        }

        var wallId = WallId;
        searchPulses.Open(wallId, () => PulseLoop.Start(
            Cache, Pulse, wallId, [PulseTopics.HoldSearch], () => Searching, InvokeAsync, () => RefreshSearchAsync(wallId), Logger));
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
