using System.Collections.Concurrent;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Compute;

/// <summary>
/// Hands out one <see cref="ComputeJobClient"/> per configured worker, each over its own named
/// <see cref="HttpClient"/>. Adding a worker = a <see cref="ComputeServiceKind"/> value, its settings
/// block and a line in <see cref="SettingsFor"/>.
/// </summary>
public sealed class ComputeJobClientFactory(
    IHttpClientFactory httpClientFactory,
    BlocwerkSettings settings,
    ILoggerFactory loggerFactory) : IComputeJobClientFactory
{
    private readonly ConcurrentDictionary<ComputeServiceKind, bool> reported = new();

    public IComputeJobClient Get(ComputeServiceKind service)
    {
        var serviceSettings = SettingsFor(settings, service);
        if (serviceSettings.ConfigurationError is { } problem && reported.TryAdd(service, true))
        {
            loggerFactory.CreateLogger<ComputeJobClientFactory>().LogError(
                "{Service} compute worker is switched off: {Problem}", service, problem);
        }

        var http = httpClientFactory.CreateClient(HttpClientName(service));
        http.Timeout = serviceSettings.RequestTimeout;
        return new ComputeJobClient(service, http, serviceSettings, loggerFactory.CreateLogger<ComputeJobClient>());
    }

    public static string HttpClientName(ComputeServiceKind service) => $"compute-{service.ToString().ToLowerInvariant()}";

    public static ComputeServiceSettings SettingsFor(BlocwerkSettings settings, ComputeServiceKind service) => service switch
    {
        ComputeServiceKind.Geometry => settings.GeometryService,
        ComputeServiceKind.Splat => settings.SplatService,
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };

    /// <summary>Registers the named HTTP clients and the factory.</summary>
    public static void Register(IServiceCollection services)
    {
        foreach (var service in Enum.GetValues<ComputeServiceKind>())
        {
            services.AddHttpClient(HttpClientName(service));
        }

        services.AddSingleton<IComputeJobClientFactory, ComputeJobClientFactory>();
    }
}
