using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Detection.Gating;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Helpers;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.HoldDetection.Outlines;
using Blocwerk.HoldDetection.Segmentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.HoldDetection;

public static class HoldDetectionServices
{
    public static IHostApplicationBuilder ConfigureHoldDetection(this IHostApplicationBuilder builder, HoldDetectionSettings settings)
    {
        var modelPath = ResolveModelPath(settings.ModelPath);

        var detector = new YoloHoldDetectionService(modelPath, settings.Tiling);
        builder.Services.AddSingleton<IHoldDetectionService>(services => new GatedHoldDetectionService(
            detector, Gate(services), settings, services.GetRequiredService<ILogger<GatedHoldDetectionService>>()));
        builder.Services.AddSingleton<IImageAlignmentService, SkiaImageAlignmentService>();

        // Cross-panel hold re-recognition for big walls. Stateless in-process OpenCV, so a singleton.
        builder.Services.AddSingleton<IHoldOverlapMatcher>(services => new GatedHoldOverlapMatcher(new OpenCvHoldOverlapMatcher(), Gate(services)));
        builder.Services.AddSingleton<IHoldPresenceProbe>(services => new GatedHoldPresenceProbe(new OpenCvHoldPresenceProbe(), Gate(services)));

        // Panel photo ↔ 3D-model facet texture registration ("place existing holds on the 3D model").
        builder.Services.AddSingleton<IPhotoTextureMatcher>(services => new GatedPhotoTextureMatcher(new OpenCvPhotoTextureMatcher(), Gate(services)));

        // ArUco markers ("glyphs") and hold outlines + fingerprints. Registration only: marker work runs
        // solely for walls that declare markers; outlines apply to every wall.
        builder.Services.AddMarkerDetection();
        builder.Services.AddSingleton<IHoldOutlineService>(services => new GatedHoldOutlineService(
            Outliner(settings.Segmenter, detector, services.GetRequiredService<ILoggerFactory>().CreateLogger<HoldSegmenter>()),
            Gate(services)));
        builder.Services.AddSingleton<IHoldOutlineBatch>(services => new SessionHoldOutlineBatch(services.GetRequiredService<IHoldOutlineService>(), Gate(services)));
        builder.Services.AddHoldOutlines();

        return builder;
    }

    /// <summary>
    /// The outline service with the segmentation model behind it. The detector loads first: it is the one that has to
    /// create the process's ONNX runtime, and it cannot once another model has.
    /// </summary>
    /// <param name="settings">The segmenter settings.</param>
    /// <param name="detector">The hold detector, or null when the caller has none.</param>
    /// <param name="logger">Hears about model files that cannot be loaded.</param>
    /// <returns>The service; it outlines by colour while the model is switched off, its files are missing or they cannot be loaded.</returns>
    public static OpenCvHoldOutlineService Outliner(HoldSegmenterSettings settings, YoloHoldDetectionService? detector = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Enabled
            ? new OpenCvHoldOutlineService(new HoldSegmenter(
                ResolveModelPath(settings.EncoderPath), ResolveModelPath(settings.DecoderPath), settings.Threads, detector is null ? null : detector.Warm, logger))
            : new OpenCvHoldOutlineService();
    }

    // Every heavy recognition entry point shares the one process-wide gate (registered by the core services).
    private static IImageRecognitionGate Gate(IServiceProvider services) => services.GetRequiredService<IImageRecognitionGate>();

    public static string ResolveModelPath(string configuredPath) =>
        ModelPathResolver.Resolve(configuredPath);
}
