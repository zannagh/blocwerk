using System.Reflection;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.HoldDetection.Outlines;
using Blocwerk.HoldDetection.Segmentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.HoldDetection;

public static class HoldDetectionServices
{
    public static IHostApplicationBuilder ConfigureHoldDetection(this IHostApplicationBuilder builder, BlocwerkSettings settings)
    {
        var modelPath = ResolveModelPath(settings.HoldDetection.ModelPath);

        var detector = new YoloHoldDetectionService(modelPath, settings.HoldDetection.Tiling);
        builder.Services.AddSingleton<IHoldDetectionService>(detector);
        builder.Services.AddSingleton<IImageAlignmentService, SkiaImageAlignmentService>();

        // Cross-panel hold re-recognition for big walls. Stateless in-process OpenCV, so a singleton.
        builder.Services.AddSingleton<IHoldOverlapMatcher, OpenCvHoldOverlapMatcher>();
        builder.Services.AddSingleton<IHoldPresenceProbe, OpenCvHoldPresenceProbe>();

        // Panel photo ↔ 3D-model facet texture registration ("place existing holds on the 3D model").
        builder.Services.AddSingleton<IPhotoTextureMatcher, OpenCvPhotoTextureMatcher>();

        // ArUco markers ("glyphs") and hold outlines + fingerprints. Registration only: marker work runs
        // solely for walls that declare markers; outlines apply to every wall.
        builder.Services.AddMarkerDetection();
        builder.Services.AddSingleton<IHoldOutlineService>(services =>
            Outliner(settings.HoldDetection.Segmenter, detector, services.GetRequiredService<ILoggerFactory>().CreateLogger<HoldSegmenter>()));
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

    public static string ResolveModelPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var resolved = Path.Combine(assemblyDir, configuredPath);
        return File.Exists(resolved) ? resolved : configuredPath;
    }
}
