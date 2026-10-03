using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Markers;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.HoldDetection;

public static class MarkerDetectionServices
{
    /// <summary>
    /// Registers <see cref="IMarkerDetectionService"/> (glyph/ArUco markers). Stateless in-process
    /// OpenCV, so a singleton. Experimental: registering it changes no existing behaviour.
    /// </summary>
    public static IServiceCollection AddMarkerDetection(this IServiceCollection services)
    {
        services.AddSingleton<IMarkerDetectionService, ArucoMarkerDetectionService>();
        return services;
    }
}
