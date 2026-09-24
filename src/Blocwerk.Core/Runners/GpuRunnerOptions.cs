// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Runners;

/// <summary>When a capture's photo-real view goes to a 3D runner instead of the all-in-one splat worker.</summary>
public enum GpuRunnerMode
{
    /// <summary>Runners when the wall has one (its own, or a shared runner exists); else the all-in-one worker.</summary>
    Auto,

    /// <summary>Always prepare on the server and wait for a runner (the splat worker runs in cpu mode).</summary>
    Always,

    /// <summary>Never: the splat worker trains itself (the pre-runner behaviour).</summary>
    Off,
}

/// <summary>
/// Settings of the runner job queue, from <c>Blocwerk:Runners:*</c> or <c>RUNNERS__*</c>
/// (<c>RUNNERS__MODE</c>, <c>RUNNERS__MAXRESULTMB</c>).
/// </summary>
public sealed class GpuRunnerOptions
{
    /// <summary>Whether (and when) to use runners; see <see cref="GpuRunnerMode"/>.</summary>
    public GpuRunnerMode Mode { get; init; } = GpuRunnerMode.Auto;

    /// <summary>A runner is "online" when its last call is at most this old.</summary>
    public TimeSpan OnlineWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a claim holds without a progress report.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many claims a job gets before it fails for good.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>How long <c>claim</c> waits for work before answering 204.</summary>
    public TimeSpan ClaimWait { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Largest trained splat a runner may upload.</summary>
    public long MaxResultBytes { get; init; } = 2048L * 1024 * 1024;

    /// <summary>How often the sweep requeues expired leases and refreshes the waiting captures' text.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(15);

    public static GpuRunnerOptions Bind(IConfiguration? configuration)
    {
        var defaults = new GpuRunnerOptions();
        var mode = Enum.TryParse<GpuRunnerMode>(Read(configuration, "Mode"), ignoreCase: true, out var m) ? m : defaults.Mode;
        var mb = int.TryParse(Read(configuration, "MaxResultMb"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                 && v is >= 1 and <= 16 * 1024
            ? v * 1024L * 1024
            : defaults.MaxResultBytes;
        return new GpuRunnerOptions { Mode = mode, MaxResultBytes = mb };
    }

    private static string? Read(IConfiguration? configuration, string key) =>
        configuration?[$"Blocwerk:Runners:{key}"] ?? Environment.GetEnvironmentVariable($"RUNNERS__{key.ToUpperInvariant()}");
}
