// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The wall's "3D runners" card: runners that can serve it (its own and shared ones) with their
/// online state and current job, the wall's waiting photo-real jobs, and creating a runner (whose
/// key and run commands are shown exactly once). Refreshes every 5 s while open.
/// </summary>
public partial class WallGpuRunnersPanel : IAsyncDisposable
{
    public const string DockerImage = "ghcr.io/zannagh/blocwerk-splat-worker:latest";

    private readonly CancellationTokenSource disposed = new();
    private IReadOnlyList<GpuRunnerInfo>? runners;
    private IReadOnlyList<GpuJobInfo>? jobs;
    private GpuRunnerCreated? created;
    private string name = string.Empty;
    private string? error;
    private string copyLabel = "Copy key";
    private bool busy;

    [Parameter]
    [EditorRequired]
    public Guid WallId { get; set; }

    [Inject]
    private IGpuRunnerService Runners { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ILogger<WallGpuRunnersPanel> Logger { get; set; } = default!;

    public async ValueTask DisposeAsync()
    {
        await disposed.CancelAsync();
        disposed.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static string Ago(DateTimeOffset? at)
    {
        if (at is null)
        {
            return "never";
        }

        var age = DateTimeOffset.UtcNow - at.Value;
        return age.TotalSeconds < 60 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 48 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} days ago";
    }

    protected override void OnInitialized() => _ = RefreshLoopAsync(disposed.Token);

    protected override Task OnParametersSetAsync() => ReloadAsync();

    private string ServerUrl => Navigation.BaseUri.TrimEnd('/');

    private string DockerCommand(string key) =>
        $"docker run -d --name blocwerk-runner --restart unless-stopped --gpus all -e BWR_KEY={key} {DockerImage} "
        + $"python -m splatworker.gpurunner --server {ServerUrl}";

    private string NativeCommand(string key) =>
        $"BWR_KEY={key} docker/splat-worker/run-runner-native.sh --server {ServerUrl}";

    private static string JobLabel(GpuJobInfo job) => job.Status switch
    {
        "Queued" => "Waiting for a 3D runner",
        _ => $"On runner “{job.RunnerName}”: {job.Stage} ({job.Progress:P0})",
    };

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await InvokeAsync(async () =>
                {
                    await ReloadAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            runners = await Runners.ListForWallAsync(WallId);
            jobs = await Runners.ListJobsForWallAsync(WallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or UserFacingException or KioskRestrictedException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Could not load the 3D runners of wall {WallId}", WallId);
            error = ex is UnauthorizedAccessException ? "Only wall admins manage 3D runners." : ex.Message;
        }
    }

    private async Task CreateAsync()
    {
        busy = true;
        error = null;
        try
        {
            created = await Runners.CreateAsync(WallId, name);
            name = string.Empty;
            copyLabel = "Copy key";
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or UserFacingException or KioskRestrictedException)
        {
            error = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }

    private void Dismiss() => created = null;

    private void ShowError(string message) => error = message;

    private async Task CopyAsync(string text)
    {
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
            copyLabel = "Copied";
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            copyLabel = "Copy failed - select the text and copy it manually";
        }
    }
}
