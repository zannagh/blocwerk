// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Web.Components.Shared;

/// <summary>The capture list's refresh loop while a capture runs (see the class remarks).</summary>
public partial class WallCaptureStatusList
{
    /// <summary>
    /// Follows the running capture. A failed poll (a DB timeout, say) is logged and retried with a
    /// growing delay instead of faulting this fire-and-forget task, which would silently stop polling.
    /// </summary>
    private async Task PollAsync(CancellationToken ct)
    {
        var failures = 0;
        try
        {
            while (ShouldPoll)
            {
                await WaitAsync(failures, ct);
                var watched = running?.Id;
                var resolving = history.Where(c => c.ModelResolving).ToDictionary(c => c.Id, c => c.GeometryModelId);
                try
                {
                    await InvokeAsync(LoadAsyncWithoutRestart);
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    Logger.LogWarning(ex, "Polling captures of wall {WallId} failed ({Failures} in a row); retrying", WallId, failures);
                    continue;
                }

                var finished = watched is null ? null : history.FirstOrDefault(c => c.Id == watched);

                // The model is live once the photo-real stage starts; that stage can take an hour.
                if (finished is { GeometryModelId: not null } && (!finished.IsRunning || finished.Status == WallCaptureStatus.Splatting)
                    && notified.Add(finished.Id))
                {
                    await InvokeAsync(() => OnCompleted.InvokeAsync());
                }
                else if (history.Any(c => resolving.TryGetValue(c.Id, out var before) && c.GeometryModelId != before))
                {
                    // A model solved again is live: the 3D view reloads as after a capture.
                    await InvokeAsync(() => OnCompleted.InvokeAsync());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Component disposed.
        }
    }

    /// <summary>The 2 s poll's backoff after consecutive failures: doubling, up to 30 s.</summary>
    private static TimeSpan PollDelay(int failures) => TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, failures)));

    // A pulse, or the safety poll; after a failed read the backoff alone, so a broken read is not retried per pulse.
    private async Task WaitAsync(int failures, CancellationToken ct)
    {
        if (failures > 0 || wake.Current is not { } current)
        {
            await Task.Delay(failures > 0 ? PollDelay(failures) : PulseWake.SafetyPoll, ct);
            return;
        }

        await current.WaitAsync(PulseWake.SafetyPoll, ct);
    }

    private async Task LoadAsyncWithoutRestart()
    {
        try
        {
            history = await Captures.GetCapturesAsync(WallId);
            running = history.FirstOrDefault(c => c.IsRunning);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Polling captures of wall {WallId} failed", WallId);
            running = null;
            history = [];
        }

        StateHasChanged();
    }
}
