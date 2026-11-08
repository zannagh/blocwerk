// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// Deadlines and budgets of the recognition queue, from <c>Blocwerk:Recognition:*</c> or <c>RECOGNITION__*</c>
/// (<c>RECOGNITION__MODE</c>, <c>RECOGNITION__INSTANCEKEY</c>, <c>RECOGNITION__INSTANCEKEYFILE</c>,
/// <c>RECOGNITION__INSTANCEKEYFILEOWNER</c>, <c>RECOGNITION__MODELSDIR</c>,
/// <c>RECOGNITION__INTERACTIVECLAIMSECONDS</c>, <c>RECOGNITION__INPUTRETENTIONHOURS</c>, <c>RECOGNITION__MAXINPUTMB</c>).
/// The lane values are the protocol's (runner protocol 1.2, §1.4 "Lanes and leases").
/// </summary>
public sealed class RecognitionQueueOptions
{
    /// <summary>The shortest instance key accepted; a shorter one is refused (no instance runner).</summary>
    public const int MinInstanceKeyLength = 32;

    /// <summary>The longest instance key accepted (the runner API reads no longer bearer key); a longer one is refused.</summary>
    public const int MaxInstanceKeyLength = 256;

    /// <summary>Where recognition runs; see <see cref="RecognitionMode"/>.</summary>
    public RecognitionMode Mode { get; init; } = RecognitionMode.InProcess;

    /// <summary>
    /// Hex SHA-256 of <c>RECOGNITION__INSTANCEKEY</c>, the instance runner's key (§1.5); null without one. The key itself is
    /// hashed as it is read and kept nowhere.
    /// </summary>
    public string? InstanceKeyHash { get; init; }

    /// <summary>Why a configured <c>RECOGNITION__INSTANCEKEY</c> was refused (<see cref="InstanceKeyPolicy"/>), or null.</summary>
    public string? InstanceKeyRefused { get; init; }

    /// <summary>
    /// Without <c>RECOGNITION__INSTANCEKEY</c>: the file the instance key is read from, generated at the first start
    /// (<see cref="InstanceKeyFile"/>). Null: no file, no instance runner without a configured key.
    /// </summary>
    public string? InstanceKeyFile { get; init; }

    /// <summary>The uid a generated key file is handed to when the app runs as root (the recognition worker's user).</summary>
    public int? InstanceKeyFileOwner { get; init; }

    /// <summary>The directory <c>GET /api/runners/models/{sha256}</c> serves model files from (only files directly in it).</summary>
    public string ModelsDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>An interactive job nobody claimed within this is cancelled ("recognition unavailable").</summary>
    public TimeSpan InteractiveClaimWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// In <c>queue+fallback</c>, an instance runner (the recognition pod) not seen for longer than this counts as gone: the
    /// app runs the job itself at once instead of waiting out the claim deadline first.
    /// </summary>
    public TimeSpan InstanceStaleAfter { get; init; } = TimeSpan.FromSeconds(60);

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

    /// <summary>How long an interactive claim may run (<c>detect-holds@2</c>: per photo).</summary>
    public TimeSpan InteractiveRun { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long an interactive <c>detect-holds@2</c> claim may run per photo.</summary>
    public TimeSpan DetectRunPerPhoto { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How long a background claim may run.</summary>
    public TimeSpan BackgroundRun { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How often the sweep runs; short, so the interactive claim deadline holds to a few seconds.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The lease of a claim in <paramref name="lane"/>.</summary>
    public TimeSpan LeaseOf(RecognitionLane lane) => lane == RecognitionLane.Interactive ? InteractiveLease : BackgroundLease;

    /// <summary>The attempt budget of <paramref name="lane"/>.</summary>
    public int AttemptsOf(RecognitionLane lane) => lane == RecognitionLane.Interactive ? InteractiveAttempts : BackgroundAttempts;

    /// <summary>How long a claim of <paramref name="kind"/> in <paramref name="lane"/> with <paramref name="photos"/> photos may run.</summary>
    public TimeSpan RunOf(string kind, RecognitionLane lane, int photos) =>
        lane == RecognitionLane.Background ? BackgroundRun
        : kind == RecognitionKinds.DetectHolds ? DetectRunPerPhoto * Math.Max(1, photos)
        : InteractiveRun;

    public static RecognitionQueueOptions Bind(IConfiguration? configuration)
    {
        var d = new RecognitionQueueOptions();
        var key = Text(configuration, "InstanceKey");
        var refused = string.IsNullOrEmpty(key) ? null : InstanceKeyPolicy.Refusal(key);
        return new RecognitionQueueOptions
        {
            Mode = ParseMode(Text(configuration, "Mode")),
            InstanceKeyHash = !string.IsNullOrEmpty(key) && refused is null ? Runners.GpuRunnerTokens.Hash(key) : null,
            InstanceKeyRefused = refused,
            InstanceKeyFile = Text(configuration, "InstanceKeyFile") is { Length: > 0 } file ? Path.GetFullPath(file) : null,
            InstanceKeyFileOwner = Int(configuration, "InstanceKeyFileOwner", 1, int.MaxValue),
            ModelsDirectory = Text(configuration, "ModelsDir") is { Length: > 0 } dir ? Path.GetFullPath(dir) : d.ModelsDirectory,
            InteractiveClaimWait = Int(configuration, "InteractiveClaimSeconds", 1, 3600) is { } s ? TimeSpan.FromSeconds(s) : d.InteractiveClaimWait,
            InputRetention = Int(configuration, "InputRetentionHours", 1, 24 * 30) is { } h ? TimeSpan.FromHours(h) : d.InputRetention,
            MaxInputBytes = Int(configuration, "MaxInputMb", 1, 16 * 1024) is { } mb ? mb * 1024L * 1024 : d.MaxInputBytes,
        };
    }

    /// <summary><c>inprocess</c> (default, also for anything unknown), <c>queue+fallback</c> or <c>queue</c>.</summary>
    public static RecognitionMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "queue+fallback" => RecognitionMode.QueueWithFallback,
        "queue" => RecognitionMode.Queue,
        _ => RecognitionMode.InProcess,
    };

    private static string? Text(IConfiguration? configuration, string key) =>
        configuration?[$"Blocwerk:Recognition:{key}"] ?? Environment.GetEnvironmentVariable($"RECOGNITION__{key.ToUpperInvariant()}");

    private static int? Int(IConfiguration? configuration, string key, int min, int max) =>
        int.TryParse(Text(configuration, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : null;
}
