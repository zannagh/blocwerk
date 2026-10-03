// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The runner overview list (<see cref="IGpuRunnerOverviewService"/>), polled every <see cref="RefreshEvery"/> while it is on
/// screen (<see cref="ShownPoller"/>). Revoking (owner or site admin, confirmed with a second tap) and sharing (site admin)
/// go through <see cref="IGpuRunnerService"/>, which checks the same rights again.
/// </summary>
public partial class RunnerOverviewList : IAsyncDisposable
{
    /// <summary>How often the list refreshes while it is shown.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource disposed = new();
    private ElementReference root;
    private GpuRunnerOverview? overview;
    private Guid? confirmRevoke;
    private string? error;
    private bool busy;

    [Inject]
    private IGpuRunnerOverviewService Overview { get; set; } = default!;

    [Inject]
    private IGpuRunnerService Runners { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ILogger<RunnerOverviewList> Logger { get; set; } = default!;

    public async ValueTask DisposeAsync()
    {
        // Cancelled, not disposed: the refresh loop may still observe its token.
        await disposed.CancelAsync();
        GC.SuppressFinalize(this);
    }

    internal static string Summary(GpuRunnerOverview o)
    {
        int Count(string state) => o.Runners.Count(r => r.State == state);
        return $"{Count(GpuRunnerStates.Online)} online, {Count(GpuRunnerStates.Paused)} paused, {Count(GpuRunnerStates.Offline)} offline"
            + (o.IsAppAdmin ? $", {Count(GpuRunnerStates.Revoked)} revoked" : string.Empty);
    }

    internal static string Hardware(GpuRunnerCapabilities c)
    {
        var parts = new[]
        {
            c.GpuName,
            c.VramMb is { } vram ? string.Create(CultureInfo.InvariantCulture, $"{vram / 1024.0:0.#} GB") : null,
            c.MaxQuality is { } q ? $"up to {q}" : null,
            c.RunnerVersion is { } v ? $"v{v}" : null,
            c.Platform,
        };
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length > 0 ? text : "not reported yet";
    }

    internal static string Walls(GpuRunnerOverviewRow r)
    {
        var names = r.Walls.Select(w => w.Approved ? $"{w.Name} (approved)" : w.Name ?? "?").ToList();
        if (r.OtherWallCount > 0)
        {
            names.Add($"{r.OtherWallCount} other wall{(r.OtherWallCount == 1 ? string.Empty : "s")}");
        }

        return names.Count == 0 ? "serves no wall" : "serves " + string.Join(", ", names);
    }

    internal static string Doing(GpuRunnerActivity? current)
    {
        if (current is null)
        {
            return "Idle";
        }

        if (current.OtherWall || current.Job is not { } job)
        {
            return current.OtherWall ? "Busy with another wall's job" : "Holds a job";
        }

        var parts = new List<string> { $"{job.WallName ?? "?"}: {job.Detail ?? job.Stage}" };
        if (job.EtaSeconds is { } eta)
        {
            parts.Add($"about {JobProgressList.Duration(eta)} left");
        }

        if (current.LeaseExpiresAt is { } lease)
        {
            parts.Add($"lease until {lease.ToLocalTime():HH:mm}");
        }

        return string.Join(" · ", parts);
    }

    internal static string Failures(GpuRunnerFailures f) =>
        $"{f.Count} failed training{(f.Count == 1 ? string.Empty : "s")} recently; last {Ago(f.LastAt)}: {f.LastReason ?? "no reason given"}";

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            _ = new ShownPoller(JS, () => root, InvokeAsync, LoadAsync, Logger).RunAsync(RefreshEvery, disposed.Token);
        }
    }

    private static string Owner(GpuRunnerOverviewRow r) => r.IsMine ? "yours" : $"owned by {r.OwnerName}";

    private static string Ago(DateTimeOffset? at) => Pages.Walls.WallGpuRunnersPanel.Ago(at);

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        try
        {
            overview = await Overview.GetAsync(ct);
            error = null;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not read the runner overview");
            error = ex is UnauthorizedAccessException or KioskRestrictedException
                ? "Only admins see 3D runners."
                : $"{UserFacingException.GenericMessage} Retrying.";
            return false;
        }
        finally
        {
            StateHasChanged();
        }
    }

    private async Task RevokeAsync(GpuRunnerOverviewRow runner)
    {
        if (confirmRevoke != runner.Id)
        {
            confirmRevoke = runner.Id;
            return;
        }

        confirmRevoke = null;
        await ChangeAsync(() => Runners.RevokeAsync(runner.Id));
    }

    private Task ShareAsync(GpuRunnerOverviewRow runner) => ChangeAsync(() => Runners.SetSharedAsync(runner.Id, !runner.SharedWithOtherWalls));

    private async Task ChangeAsync(Func<Task> change)
    {
        busy = true;
        try
        {
            await change();
            await LoadAsync(disposed.Token);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or UserFacingException or KioskRestrictedException
                                       or ApiKeySessionRestrictedException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "A runner change was refused");
            error = ex is UserFacingException or ApiKeySessionRestrictedException ? ex.Message : "That is not allowed for this runner.";
        }
        finally
        {
            busy = false;
        }
    }
}
