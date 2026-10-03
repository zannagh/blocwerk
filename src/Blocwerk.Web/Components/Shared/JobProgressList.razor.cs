// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The list behind the administration's "Background jobs" page and the wall's compact panel: every job the user may watch
/// (<see cref="IJobProgressService"/>), polled every <see cref="RefreshEvery"/> while the list is on screen (the tab is
/// visible and the panel open, see <c>element-shown.js</c>).
/// </summary>
public partial class JobProgressList : IAsyncDisposable
{
    /// <summary>How often the list refreshes while it is shown.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(4);

    private readonly CancellationTokenSource disposed = new();
    private ElementReference root;
    private JobProgressSnapshot? snapshot;
    private string? error;
    private bool showRecent = true;

    /// <summary>Only this wall's jobs (null: every wall the user may watch).</summary>
    [Parameter]
    public Guid? WallId { get; set; }

    /// <summary>How far back ended jobs are listed when "include recent" is on.</summary>
    [Parameter]
    public int RecentHours { get; set; } = 24;

    [Inject]
    private IJobProgressService Jobs { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ILogger<JobProgressList> Logger { get; set; } = default!;

    private int RunningCount => snapshot?.Jobs.Count(j => j.State == JobStates.Running) ?? 0;

    public async ValueTask DisposeAsync()
    {
        await disposed.CancelAsync();
        disposed.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The line under a job: when it started, when it last moved or ended, and the remaining time.</summary>
    internal static string Meta(JobProgressItem job)
    {
        var parts = new List<string>();
        if (job.Percent is { } percent)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{percent:0.#} %"));
        }

        if (job is { Step: { } step, TotalSteps: { } total })
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"step {step:N0}/{total:N0}"));
        }

        if (job.EtaSeconds is { } eta)
        {
            parts.Add($"about {Duration(eta)} left{(job.EtaSource == JobEtaSources.History ? " (usual time)" : string.Empty)}");
        }

        parts.Add(job.StartedAt is { } started ? $"started {Ago(started)}" : "start unknown");
        parts.Add(job.EndedAt is { } ended ? $"ended {Ago(ended)}" : job.UpdatedAt is { } updated ? $"updated {Ago(updated)}" : string.Empty);
        if (job.RunnerName is not null)
        {
            parts.Add($"on {job.RunnerName}");
        }

        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    /// <summary>A remaining time in words: seconds under a minute, minutes under two hours, hours beyond.</summary>
    internal static string Duration(double seconds) => seconds switch
    {
        < 60 => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(seconds))} s"),
        < 7200 => string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(seconds / 60)} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:0.#} h"),
    };

    internal static string KindLabel(string kind) => kind switch
    {
        JobKinds.Capture => "Capture",
        JobKinds.GpuTraining => "Photo-real training",
        JobKinds.Finish => "Finishing the view",
        JobKinds.FollowUp => "Follow-up step",
        JobKinds.TextureRerender => "Textures again",
        JobKinds.Resolve => "Model solved again",
        JobKinds.Import => "Capture import",
        _ => kind,
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await ReloadAsync();
            StateHasChanged();
            _ = RefreshLoopAsync(disposed.Token);
        }
    }

    private static string Ago(DateTimeOffset at) => Pages.Walls.WallGpuRunnersPanel.Ago(at);

    private async Task ToggleRecentAsync(ChangeEventArgs e)
    {
        showRecent = e.Value is true;
        await ReloadAsync();
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(RefreshEvery);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await InvokeAsync(async () =>
                {
                    if (await IsShownAsync())
                    {
                        await ReloadAsync();
                        StateHasChanged();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private async Task<bool> IsShownAsync()
    {
        try
        {
            return await JS.InvokeAsync<bool>("bwShown", disposed.Token, root);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            snapshot = await Jobs.ListAsync(WallId, showRecent ? TimeSpan.FromHours(RecentHours) : TimeSpan.Zero, disposed.Token);
            error = null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KioskRestrictedException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Could not list the background jobs (wall {WallId})", WallId);
            error = ex is InvalidOperationException ? UserFacingException.GenericMessage : "Only admins see background jobs.";
        }
        catch (OperationCanceledException)
        {
            // Disposed while loading.
        }
    }
}
