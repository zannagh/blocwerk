// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Recognition.Worker;

/// <summary>
/// The worker's settings, from <c>Recognition:*</c> or the environment: <c>RECOGNITION__SERVER</c> (the app's base URL),
/// <c>RECOGNITION__RUNNERKEY</c> (a <c>bwr_</c> key, external runners) or <c>RECOGNITION__INSTANCEKEY</c> (the instance
/// runner, runner protocol §1.5) or else <c>RECOGNITION__INSTANCEKEYFILE</c> (the file the app generates that key into,
/// read once it exists), <c>RECOGNITION__MODELSDIR</c> (read-only models mount), <c>RECOGNITION__MODELCACHEDIR</c>
/// (models fetched by hash), <c>RECOGNITION__WARMMODEL</c> (loaded at start) and <c>JOB_MAX_MEMORY_MB</c> (0: no watchdog).
/// </summary>
public sealed record WorkerOptions
{
    /// <summary>The runner protocol version this worker speaks.</summary>
    public const string ProtocolVersion = "1.2";

    public required Uri Server { get; init; }

    /// <summary>Gets the runner key; empty while it is still to be read from <see cref="KeyFile"/>.</summary>
    public required string Key { get; init; }

    public string? KeyFile { get; init; }

    /// <summary>Gets the per-process <c>X-Blocwerk-Claim</c> token, so replicas can share one key.</summary>
    public string ClaimToken { get; init; } = Guid.NewGuid().ToString("N");

    public string ModelsDirectory { get; init; } = "/models";

    public string ModelCacheDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "blocwerk-models");

    public string? WarmModel { get; init; } = "climbingcrux.onnx";

    /// <summary>Gets the memory one job may push the container to, MB; 0 turns the watchdog off.</summary>
    public long JobMaxMemoryMb { get; init; }

    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how long the worker waits before exiting with code 2 or 3, so a restart loop does not hammer the app.</summary>
    public TimeSpan ExitBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long memory may stay above the limit after the watchdog cancelled a job before the process exits.</summary>
    public TimeSpan MemoryReleaseGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the longest one claim long-poll may take (the server answers within ~25 s).</summary>
    public TimeSpan ClaimTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Gets how long without a claim attempt or heartbeat before <c>GET /health</c> fails (liveness).</summary>
    public TimeSpan LivenessTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how long without a server answer before the worker reads as not ready.</summary>
    public TimeSpan ContactTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public static WorkerOptions Bind(IConfiguration configuration)
    {
        var d = new WorkerOptions { Server = new Uri("http://localhost"), Key = string.Empty };
        var key = Text(configuration, "Recognition:RunnerKey") ?? Text(configuration, "Recognition:InstanceKey");
        var keyFile = key is null ? Text(configuration, "Recognition:InstanceKeyFile") : null;
        if (key is null && keyFile is null)
        {
            throw new InvalidOperationException(
                "Set RECOGNITION__INSTANCEKEY or RECOGNITION__INSTANCEKEYFILE (instance runner) or RECOGNITION__RUNNERKEY (bwr_ key).");
        }

        return new WorkerOptions
        {
            Server = new Uri(Text(configuration, "Recognition:Server") ?? "http://blocwerk:5050", UriKind.Absolute),
            Key = key ?? string.Empty,
            KeyFile = keyFile,
            ModelsDirectory = Text(configuration, "Recognition:ModelsDir") ?? d.ModelsDirectory,
            ModelCacheDirectory = Text(configuration, "Recognition:ModelCacheDir") ?? d.ModelCacheDirectory,
            WarmModel = Text(configuration, "Recognition:WarmModel") ?? d.WarmModel,
            JobMaxMemoryMb = long.TryParse(Text(configuration, "JOB_MAX_MEMORY_MB"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) && mb > 0
                ? mb
                : 0,
        };
    }

    private static string? Text(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value.Trim() : null;
}
