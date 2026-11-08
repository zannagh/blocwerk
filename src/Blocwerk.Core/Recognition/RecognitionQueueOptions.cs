// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// Deadlines and budgets of the recognition queue, from <c>Blocwerk:Recognition:*</c> or <c>RECOGNITION__*</c>
/// (<c>RECOGNITION__INTERACTIVECLAIMSECONDS</c>, <c>RECOGNITION__INPUTRETENTIONHOURS</c>, <c>RECOGNITION__MAXINPUTMB</c>).
/// The lane values are the protocol's (runner protocol 1.2, §1.4 "Lanes and leases").
/// </summary>
public sealed class RecognitionQueueOptions
{
    /// <summary>An interactive job nobody claimed within this is cancelled ("recognition unavailable").</summary>
    public TimeSpan InteractiveClaimWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The lease of an interactive claim (heartbeat every 5 s).</summary>
    public TimeSpan InteractiveLease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The lease of a background claim (heartbeat every 30 s).</summary>
    public TimeSpan BackgroundLease { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Claims an interactive job may use before its error goes to the page.</summary>
    public int InteractiveAttempts { get; init; } = 2;

    /// <summary>Claims a background job may use before it fails.</summary>
    public int BackgroundAttempts { get; init; } = 3;

    /// <summary>How long an input envelope (photo copies) may stay in the store; the sweep drops older ones.</summary>
    public TimeSpan InputRetention { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The largest input envelope.</summary>
    public long MaxInputBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>How often a waiting caller re-reads its job, in case a completion signal was missed.</summary>
    public TimeSpan WaiterPoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often the sweep runs; short, so the interactive claim deadline holds to a few seconds.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The lease of a claim in <paramref name="lane"/>.</summary>
    public TimeSpan LeaseOf(RecognitionLane lane) => lane == RecognitionLane.Interactive ? InteractiveLease : BackgroundLease;

    /// <summary>The attempt budget of <paramref name="lane"/>.</summary>
    public int AttemptsOf(RecognitionLane lane) => lane == RecognitionLane.Interactive ? InteractiveAttempts : BackgroundAttempts;

    public static RecognitionQueueOptions Bind(IConfiguration? configuration)
    {
        var d = new RecognitionQueueOptions();
        return new RecognitionQueueOptions
        {
            InteractiveClaimWait = Int(configuration, "InteractiveClaimSeconds", 1, 3600) is { } s ? TimeSpan.FromSeconds(s) : d.InteractiveClaimWait,
            InputRetention = Int(configuration, "InputRetentionHours", 1, 24 * 30) is { } h ? TimeSpan.FromHours(h) : d.InputRetention,
            MaxInputBytes = Int(configuration, "MaxInputMb", 1, 16 * 1024) is { } mb ? mb * 1024L * 1024 : d.MaxInputBytes,
        };
    }

    private static int? Int(IConfiguration? configuration, string key, int min, int max) =>
        int.TryParse(
            configuration?[$"Blocwerk:Recognition:{key}"] ?? Environment.GetEnvironmentVariable($"RECOGNITION__{key.ToUpperInvariant()}"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : null;
}
