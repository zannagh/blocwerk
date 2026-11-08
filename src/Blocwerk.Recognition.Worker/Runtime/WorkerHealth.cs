// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// What <c>GET /health</c> reports: ready once the models are warm and a hello was answered, and while the server
/// answered within <see cref="WorkerOptions.ContactTimeout"/> (the claim long-poll answers every ~25 s).
/// </summary>
public sealed class WorkerHealth(WorkerOptions options, TimeProvider time)
{
    private long lastContactTicks;

    public bool ModelsLoaded { get; private set; }

    public DateTimeOffset? LastHello { get; private set; }

    public DateTimeOffset? LastContact => Interlocked.Read(ref lastContactTicks) is > 0 and var t ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    public IReadOnlyList<string> Kinds { get; private set; } = [];

    public bool Ready => ModelsLoaded && LastHello is not null && time.GetUtcNow() - LastContact < options.ContactTimeout;

    public void MarkModelsLoaded() => ModelsLoaded = true;

    public void MarkHello(IReadOnlyList<string> kinds)
    {
        Kinds = kinds;
        LastHello = time.GetUtcNow();
        MarkContact();
    }

    public void MarkContact() => Interlocked.Exchange(ref lastContactTicks, time.GetUtcNow().UtcTicks);

    /// <summary>The body of <c>GET /health</c>, shaped like contracts <c>worker/health</c>.</summary>
    public object Describe() => new
    {
        status = Ready ? "ok" : "degraded",
        service = "recognition",
        runnerProtocol = WorkerOptions.ProtocolVersion,
        version = typeof(WorkerHealth).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        kinds = Kinds,
        modelsLoaded = ModelsLoaded,
        lastHello = LastHello,
        lastContact = LastContact,
    };
}
