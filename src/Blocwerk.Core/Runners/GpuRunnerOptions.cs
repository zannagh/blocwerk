// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Runners;

/// <summary>When a capture's photo-real view goes to a 3D runner instead of the all-in-one splat worker.</summary>
public enum GpuRunnerMode
{
    /// <summary>A runner when one that may train this wall at this quality is online now; else the all-in-one worker.</summary>
    Auto,

    /// <summary>Always prepare on the server and queue for a runner (the splat worker runs without a GPU).</summary>
    Always,

    /// <summary>Never: the splat worker trains itself (the pre-runner behaviour).</summary>
    Off,
}

/// <summary>
/// Settings of the runner job queue, from <c>Blocwerk:Runners:*</c> or <c>RUNNERS__*</c> (<c>RUNNERS__MODE</c>,
/// <c>RUNNERS__MAXRESULTMB</c>, <c>RUNNERS__MAXBUNDLEMB</c>, <c>RUNNERS__MAXATTEMPTS</c>, <c>RUNNERS__MAXLOSTLEASES</c>,
/// <c>RUNNERS__SHAREDNEEDSOPTIN</c>).
/// </summary>
public sealed class GpuRunnerOptions
{
    /// <summary>Whether (and when) to use runners; see <see cref="GpuRunnerMode"/>.</summary>
    public GpuRunnerMode Mode { get; init; } = GpuRunnerMode.Auto;

    /// <summary>A runner is "online" when its last call is at most this old.</summary>
    public TimeSpan OnlineWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a claim holds without a progress report.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Training failures a runner may report before the job fails for good.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Claims a job may lose to vanished runners (reboot, sleep, network) before it fails for good.</summary>
    public int MaxLostLeases { get; init; } = 10;

    /// <summary>
    /// Whether a shared runner needs the receiving wall's opt-in (<see cref="Entities.GpuRunnerSharedOptIn"/>).
    /// The safe default: a wall's photos only go to a machine its own admin accepted.
    /// </summary>
    public bool SharedNeedsOptIn { get; init; } = true;

    /// <summary>How long <c>claim</c> waits for work before answering 204.</summary>
    public TimeSpan ClaimWait { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Largest trained splat a runner may upload (after gzip decoding).</summary>
    public long MaxResultBytes { get; init; } = 2048L * 1024 * 1024;

    /// <summary>Largest training bundle taken from the splat worker (ultra: 4096 px photos plus video frames).</summary>
    public long MaxBundleBytes { get; init; } = 6144L * 1024 * 1024;

    /// <summary>How often the sweep requeues expired leases and refreshes the waiting jobs' text.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(15);

    public static GpuRunnerOptions Bind(IConfiguration? configuration)
    {
        var d = new GpuRunnerOptions();
        return new GpuRunnerOptions
        {
            Mode = Enum.TryParse<GpuRunnerMode>(Read(configuration, "Mode"), ignoreCase: true, out var m) ? m : d.Mode,
            MaxResultBytes = Megabytes(configuration, "MaxResultMb", 16 * 1024) ?? d.MaxResultBytes,
            MaxBundleBytes = Megabytes(configuration, "MaxBundleMb", 64 * 1024) ?? d.MaxBundleBytes,
            MaxAttempts = Int(configuration, "MaxAttempts", 1, 100) ?? d.MaxAttempts,
            MaxLostLeases = Int(configuration, "MaxLostLeases", 1, 1000) ?? d.MaxLostLeases,
            SharedNeedsOptIn = bool.TryParse(Read(configuration, "SharedNeedsOptIn"), out var o) ? o : d.SharedNeedsOptIn,
        };
    }

    private static long? Megabytes(IConfiguration? configuration, string key, int max) =>
        Int(configuration, key, 1, max) is { } mb ? mb * 1024L * 1024 : null;

    private static int? Int(IConfiguration? configuration, string key, int min, int max) =>
        int.TryParse(Read(configuration, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : null;

    private static string? Read(IConfiguration? configuration, string key) =>
        configuration?[$"Blocwerk:Runners:{key}"] ?? Environment.GetEnvironmentVariable($"RUNNERS__{key.ToUpperInvariant()}");
}
