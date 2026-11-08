// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Recognition.Worker.Envelope;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary><c>presence</c>: <see cref="IHoldPresenceProbe.Score"/> on parts <c>old</c> and <c>new</c>.</summary>
public sealed class PresenceHandler(IHoldPresenceProbe probe) : IRecognitionJobHandler
{
    public string Kind => "presence";

    public Task<object> RunAsync(RecognitionEnvelope envelope, CancellationToken ct)
    {
        var request = envelope.Request<PresenceRequest>();
        var old = envelope.Image("old");
        var current = envelope.Image("new");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ToResult(probe.Score(old, current, request.Queries)));
    }

    public static object ToResult(IReadOnlyList<double?> scores) => new { version = 1, scores };
}
