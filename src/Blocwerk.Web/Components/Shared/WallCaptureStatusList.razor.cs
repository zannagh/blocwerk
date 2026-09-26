using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Polls the wall's captures every ~2 s while one is running (and only then), so the status panel
/// follows the background pipeline without the page doing anything.
/// </summary>
public partial class WallCaptureStatusList : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] IgnoredDetectionKinds = [WallGeometryModelCheck.KindRejectedObservation];

    private readonly CancellationTokenSource disposed = new();
    private readonly HashSet<Guid> notified = [];
    private Guid loadedWallId;
    private IReadOnlyList<WallCaptureSummary> history = [];
    private WallCaptureSummary? running;
    private string? failure;
    private Task? pollLoop;
    private bool retraining;
    private bool ultraAvailable;

    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Raised once when a running capture has an active model (finished, or on to the photo-real stage).</summary>
    [Parameter]
    public EventCallback OnCompleted { get; set; }

    [Inject]
    private IWallCaptureService Captures { get; set; } = default!;

    [Inject]
    private ILogger<WallCaptureStatusList> Logger { get; set; } = default!;

    [Inject]
    private Blocwerk.Core.Runners.IGpuRunnerService GpuRunners { get; set; } = default!;

    [Inject]
    private Blocwerk.Core.Runners.SplatQualityOffer QualityOffer { get; set; } = default!;

    /// <summary>Re-reads the history now and starts polling if a capture is running.</summary>
    public async Task RefreshAsync()
    {
        await LoadAsync();
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await disposed.CancelAsync();
        disposed.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId)
        {
            return;
        }

        loadedWallId = WallId;
        await LoadAsync();
    }

    private static string StatusLabel(WallCaptureStatus status) => status switch
    {
        WallCaptureStatus.Queued => "Waiting to start",
        WallCaptureStatus.Detecting => "Finding markers",
        WallCaptureStatus.Solving => "Computing the 3D model",
        WallCaptureStatus.Texturing => "Rendering textures",
        WallCaptureStatus.Splatting => "Model ready · making the photo-real view",
        WallCaptureStatus.Succeeded => "Model ready",
        WallCaptureStatus.SucceededWithoutTextures => "Model ready (no textures)",
        WallCaptureStatus.SucceededWithoutSplat => "Model ready (no photo-real view)",
        WallCaptureStatus.StoredNotActivated => "Model stored, not activated",
        WallCaptureStatus.Failed => "Failed",
        _ => "Uploading",
    };

    private bool CanRetrain(WallCaptureSummary capture) => Captures.IsSplatConfigured && !capture.IsRunning
        && capture.GeometryModelId is not null && capture.PhotoCount >= 2
        && capture.Status is not (WallCaptureStatus.Failed or WallCaptureStatus.StoredNotActivated);

    /// <summary>Queues a retrain of the capture's photo-real view; the current view stays until the new one is stored.</summary>
    private async Task RetrainAsync(Guid captureId, SplatQuality quality)
    {
        retraining = true;
        try
        {
            var problems = await Captures.RetrainPhotoRealAsync(captureId, quality);
            if (problems.Count > 0)
            {
                failure = string.Join(" ", problems);
                return;
            }

            await LoadAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or UserFacingException or KioskRestrictedException)
        {
            failure = ex.Message;
        }
        finally
        {
            retraining = false;
        }
    }

    /// <summary>
    /// The retrain offer after the capture's quality: the next profile, and Ultra after Max only when a runner (or the
    /// splat worker) can really train it.
    /// </summary>
    private SplatQuality? BetterQuality(WallCaptureSummary capture) =>
        CaptureSplatDocuments.NextQuality(capture.SplatQuality)
        ?? (capture.SplatQuality == SplatQuality.Max && ultraAvailable ? SplatQuality.Ultra : null);

    /// <summary>Stops waiting for a 3D runner: the capture keeps its model (and any older photo-real view).</summary>
    private async Task CancelRunnerJobAsync(Guid captureId)
    {
        retraining = true;
        try
        {
            await GpuRunners.CancelCaptureJobAsync(captureId);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or UserFacingException or KioskRestrictedException)
        {
            failure = ex.Message;
        }
        finally
        {
            retraining = false;
        }
    }

    private static string StatusClass(WallCaptureStatus status) => status switch
    {
        WallCaptureStatus.Succeeded => "glyph-active-badge",
        WallCaptureStatus.Failed => "capture-failed",
        _ => "capture-pending",
    };

    private async Task LoadAsync()
    {
        try
        {
            history = await Captures.GetCapturesAsync(WallId);
            running = history.FirstOrDefault(c => c.IsRunning);
            failure = null;
            ultraAvailable = Captures.IsSplatConfigured && await QualityOffer.UltraAvailableAsync(WallId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load captures of wall {WallId}", WallId);
            failure = "The capture history could not be loaded.";
        }

        if (running is not null && pollLoop is not { IsCompleted: false })
        {
            pollLoop = PollAsync(disposed.Token);
        }
    }

    /// <summary>
    /// Follows the running capture. A failed poll (a DB timeout, say) is logged and retried with a
    /// growing delay instead of faulting this fire-and-forget task, which would silently stop polling.
    /// </summary>
    private async Task PollAsync(CancellationToken ct)
    {
        var failures = 0;
        try
        {
            while (running is not null)
            {
                await Task.Delay(PollDelay(failures), ct);
                var watched = running.Id;
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

                var finished = history.FirstOrDefault(c => c.Id == watched);

                // The model is live once the photo-real stage starts; that stage can take an hour.
                if (finished is { GeometryModelId: not null } && (!finished.IsRunning || finished.Status == WallCaptureStatus.Splatting)
                    && notified.Add(watched))
                {
                    await InvokeAsync(() => OnCompleted.InvokeAsync());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Component disposed.
        }
    }

    /// <summary>The normal interval, doubling per consecutive failure up to 30 s.</summary>
    private static TimeSpan PollDelay(int failures) => failures == 0
        ? PollInterval
        : TimeSpan.FromSeconds(Math.Min(30, PollInterval.TotalSeconds * Math.Pow(2, failures)));

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
        }

        StateHasChanged();
    }
}
