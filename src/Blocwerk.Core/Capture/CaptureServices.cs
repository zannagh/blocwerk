using Blocwerk.Core.Compute;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Capture;

/// <summary>DI registration of the in-app glyph capture and the compute job clients.</summary>
public static class CaptureServices
{
    public static IServiceCollection AddWallCapture(this IServiceCollection services)
    {
        ComputeJobClientFactory.Register(services);
        services.AddSingleton<ICaptureFileStore, FileSystemCaptureFileStore>();
        services.AddSingleton<ICaptureVideoFrameExtractor, CaptureVideoFrameExtractor>();
        services.AddSingleton<ICapturePhotoConverter, HeifCapturePhotoConverter>();
        services.AddSingleton(sp => WallCapturePipelineOptions.Bind(sp.GetService<IConfiguration>()));
        services.AddSingleton(_ => new CaptureVideoUploadSlots());

        // Single-instance app: one in-memory queue and ONE worker; the row status is the durable truth
        // and the worker re-enqueues unfinished captures on start (see WallCaptureWorker).
        services.AddSingleton<WallCaptureQueue>();
        services.AddSingleton<WallCaptureProcessor>();

        // 3D runners: GPU machines that pull the photo-real training (see GpuJobQueue).
        services.AddSingleton(sp => Runners.GpuRunnerOptions.Bind(sp.GetService<IConfiguration>()));
        services.AddSingleton<Runners.GpuJobSignal>();
        services.AddSingleton<Runners.GpuJobQueue>();
        services.AddHostedService<Runners.GpuJobSweepWorker>();
        services.AddScoped<Runners.IGpuRunnerService, Runners.GpuRunnerService>();
        services.AddHostedService<WallCaptureWorker>();
        services.AddScoped<IWallCaptureService, WallCaptureService>();
        services.AddScoped<ICapturePanelPhotoService, CapturePanelPhotoService>();

        // Retention: stale drafts, expired photos and files no row references (see WallCaptureSweeper).
        services.AddSingleton<WallCaptureSweeper>();
        services.AddHostedService<WallCaptureSweepWorker>();

        // Level-of-detail ladders of photo-real scenes stored before the ladder existed.
        services.AddSingleton<SplatLodBackfill>();
        services.AddHostedService(sp => sp.GetRequiredService<SplatLodBackfill>());
        return services;
    }
}
