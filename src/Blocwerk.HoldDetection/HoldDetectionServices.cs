using System.Reflection;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.HoldDetection.Outlines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.HoldDetection;

public static class HoldDetectionServices
{
    public static IHostApplicationBuilder ConfigureHoldDetection(this IHostApplicationBuilder builder, BlocwerkSettings settings)
    {
        var modelPath = ResolveModelPath(settings.HoldDetection.ModelPath);

        builder.Services.AddSingleton<IHoldDetectionService>(_ => new YoloHoldDetectionService(modelPath, settings.HoldDetection.Tiling));
        builder.Services.AddSingleton<IImageAlignmentService, SkiaImageAlignmentService>();

        // Cross-panel hold re-recognition for big walls. Stateless in-process OpenCV, so a singleton.
        builder.Services.AddSingleton<IHoldOverlapMatcher, OpenCvHoldOverlapMatcher>();

        // Panel photo ↔ 3D-model facet texture registration ("place existing holds on the 3D model").
        builder.Services.AddSingleton<IPhotoTextureMatcher, OpenCvPhotoTextureMatcher>();

        // ArUco markers ("glyphs") and hold outlines + fingerprints. Registration only: marker work runs
        // solely for walls that declare markers; outlines apply to every wall.
        builder.Services.AddMarkerDetection();
        builder.Services.AddHoldOutlines();

        return builder;
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
