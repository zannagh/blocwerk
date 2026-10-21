using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Pulse;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Follows the wall's captures while one is running (and only then), so the status panel follows the background
/// pipeline without the page doing anything: it re-reads on the captures' and runner jobs' pulses
/// (<see cref="PulseWake"/>), and every <see cref="PulseWake.SafetyPoll"/> without one.
/// </summary>
public partial class WallCaptureStatusList : IAsyncDisposable
{
    private static readonly string[] IgnoredDetectionKinds = [WallGeometryModelCheck.KindRejectedObservation];

    private readonly CancellationTokenSource disposed = new();
    private readonly HashSet<Guid> notified = [];
    private readonly Dictionary<Guid, TextureRouting> routings = [];
    private Dictionary<Guid, Blocwerk.Core.Runners.GpuJobInfo> textureJobs = [];
    private Guid loadedWallId;
    private IReadOnlyList<WallCaptureSummary> history = [];
    private WallCaptureSummary? running;
    private string? failure;
    private Task? pollLoop;
    private PulseWake? wake;
    private bool retraining;
    private bool ultraAvailable;

    /// <summary>A capture is running, or its wall textures are being rendered (or its model solved) again.</summary>
    private bool ShouldPoll => running is not null || history.Any(c => c.TexturesRerendering || c.ModelResolving);

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
    private WallCacheState Cache { get; set; } = default!;

    [Inject]
    private IWallPulse Pulse { get; set; } = default!;

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
        wake?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId)
        {
            return;
        }

        loadedWallId = WallId;
        wake?.Dispose();
        wake = null;
        wake = await PulseWake.OpenAsync(Cache, Pulse, WallId, [PulseTopics.Capture, PulseTopics.GpuJob]);
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
    private Task RetrainAsync(Guid captureId, SplatQuality quality) => QueuePhotoRealAsync(() => Captures.RetrainPhotoRealAsync(captureId, quality));

    /// <summary>Queues finishing the capture's trained photo-real view again (and its follow-up steps); nothing is trained.</summary>
    private Task RefinishAsync(Guid captureId) => QueuePhotoRealAsync(() => Captures.RefinishPhotoRealAsync(captureId));

    /// <summary>Queues rendering the capture's wall textures again; its photo-real view is left alone.</summary>
    private Task RerenderTexturesAsync(Guid captureId, TextureRerenderChoice choice) =>
        QueuePhotoRealAsync(() => Captures.RerenderTexturesAsync(captureId, choice.Quality, choice.Route));

    private TextureRouting RoutingOf(Guid captureId) => routings.TryGetValue(captureId, out var found) ? found : TextureRouting.None;

    /// <summary>Loads what each texture quality would take for the capture, and where it would run (shown when the quality picker opens).</summary>
    private async Task LoadRoutingAsync(Guid captureId)
    {
        try
        {
            routings[captureId] = await Captures.GetTextureRoutingAsync(captureId);
        }
        catch (Exception ex) when (ex is UserFacingException or UnauthorizedAccessException)
        {
            routings[captureId] = TextureRouting.None;
        }
    }

    /// <summary>Where a re-render that went to a 3D runner stands: waiting for one, rendering on it, installing.</summary>
    private string? RerenderNoteOf(Guid captureId) => textureJobs.TryGetValue(captureId, out var job)
        ? job.Status switch
        {
            "Queued" => $"On a 3D runner: {job.Stage ?? "waiting for one"}.",
            "Succeeded" => "The 3D runner finished; installing the textures.",
            _ => $"On 3D runner “{job.RunnerName}”: {job.Stage} ({job.Progress:P0}).",
        }
        : null;

    /// <summary>The wall's textures jobs on 3D runners, by capture (for the re-render notes); none when they cannot be read.</summary>
    private async Task LoadTextureJobsAsync()
    {
        try
        {
            textureJobs = (await GpuRunners.ListJobsForWallAsync(WallId)).Where(j => j.Kind == "textures")
                .GroupBy(j => j.CaptureId).ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAt).First());
        }
        catch (Exception ex) when (ex is UserFacingException or UnauthorizedAccessException or KioskRestrictedException)
        {
            textureJobs = [];
        }
    }

    /// <summary>Queues solving the capture's 3D model again from its photos; its photo-real view is kept.</summary>
    private Task ResolveModelAsync(Guid captureId) => QueuePhotoRealAsync(() => Captures.ResolveModelAsync(captureId));

    private async Task QueuePhotoRealAsync(Func<Task<IReadOnlyList<string>>> queue)
    {
        retraining = true;
        try
        {
            var problems = await queue();
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
            await LoadTextureJobsAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load captures of wall {WallId}", WallId);
            failure = "The capture history could not be loaded.";
        }

        if (ShouldPoll && pollLoop is not { IsCompleted: false })
        {
            pollLoop = PollAsync(disposed.Token);
        }
    }
}
