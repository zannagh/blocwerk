using System.Reflection;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.HoldDetection;

public static class HoldDetectionServices
{
    public static IHostApplicationBuilder ConfigureHoldDetection(this IHostApplicationBuilder builder, BlocwerkSettings settings)
    {
        var modelPath = ResolveModelPath(settings.HoldDetection.ModelPath);

        builder.Services.AddSingleton<IHoldDetectionService>(_ => new YoloHoldDetectionService(modelPath));
        builder.Services.AddSingleton<IImageAlignmentService, SkiaImageAlignmentService>();

        return builder;
    }

    public static string ResolveModelPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var resolved = Path.Combine(assemblyDir, configuredPath);
        if (File.Exists(resolved))
        {
            return resolved;
        }

        return configuredPath;
    }
}
