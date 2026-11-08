// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// <c>RECOGNITION__MODE</c> in the app's services: with <c>inprocess</c> (the default) nothing changes. With
/// <c>queue+fallback</c> or <c>queue</c>, hold detection, markers, presence and the overlap pass go through the queue,
/// each wrapping the in-process service it replaces (the fallback). Outlines and texture matching stay in the app.
/// </summary>
public static class RecognitionModeServices
{
    /// <summary>Wraps the recognition services; call it after the hold detection services are registered.</summary>
    public static IHostApplicationBuilder UseRecognitionMode(this IHostApplicationBuilder builder)
    {
        if (RecognitionQueueOptions.Bind(builder.Configuration).Mode == RecognitionMode.InProcess)
        {
            return builder;
        }

        var services = builder.Services;
        services.AddSingleton<RecognitionCaller>();
        Decorate<IHoldDetectionService>(services, (sp, inner) => new QueuedHoldDetectionService(
            inner, sp.GetRequiredService<RecognitionCaller>(), sp.GetRequiredService<RunnerHoldDetection>(), sp.GetRequiredService<BlocwerkSettings>().HoldDetection));
        Decorate<IMarkerDetectionService>(services, (sp, inner) => new QueuedMarkerDetectionService(inner, sp.GetRequiredService<RecognitionCaller>()));
        Decorate<IHoldPresenceProbe>(services, (sp, inner) => new QueuedHoldPresenceProbe(inner, sp.GetRequiredService<RecognitionCaller>()));
        Decorate<IHoldOverlapMatcher>(services, (sp, inner) => new QueuedHoldOverlapMatcher(inner, sp.GetRequiredService<RecognitionCaller>()));
        return builder;
    }

    private static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> wrap)
        where T : class
    {
        if (services.LastOrDefault(d => d.ServiceType == typeof(T)) is not { } inner)
        {
            return;
        }

        services.Remove(inner);
        services.Add(ServiceDescriptor.Describe(typeof(T), sp => wrap(sp, (T)Create(sp, inner)), inner.Lifetime));
    }

    private static object Create(IServiceProvider sp, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
}
