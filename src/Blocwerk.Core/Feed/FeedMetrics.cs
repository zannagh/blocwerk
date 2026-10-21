// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Telemetry;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The feed's gauge, on the shared <see cref="Otel.Meter"/>: <c>blocwerk.feed.horizon_lag</c> in seconds, scraped as
/// <c>blocwerk_feed_horizon_lag_seconds</c>. It is how long the oldest committed but not yet servable row has waited
/// for the horizon (an older transaction still open). Alert above 30 s.
/// </summary>
public static class FeedMetrics
{
    private static double horizonLagSeconds;

    static FeedMetrics()
    {
        Otel.Meter.CreateObservableGauge(
            "blocwerk.feed.horizon_lag",
            () => Volatile.Read(ref horizonLagSeconds),
            unit: "s",
            description: "Age of the oldest committed live-update row still held back by an open older transaction.");
    }

    /// <summary>Registers the gauge (runs the type initializer).</summary>
    public static void Initialize()
    {
        // Referencing any member is enough to trigger the type initializer.
    }

    /// <summary>Publishes the latest lag.</summary>
    public static void PublishHorizonLag(double seconds) => Volatile.Write(ref horizonLagSeconds, seconds);
}
