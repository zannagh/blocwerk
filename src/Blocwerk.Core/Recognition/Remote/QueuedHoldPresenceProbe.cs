// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The presence probe as an interactive <c>presence</c> job (<see cref="IAsyncHoldPresenceProbe"/>): the job is awaited
/// without a gate slot, and only the in-app run <see cref="RecognitionCaller"/> falls back to takes one. The synchronous
/// <see cref="Score"/> runs in the app: it is only called from inside gated recognition, where waiting for a runner would
/// hold the slot.
/// </summary>
public sealed class QueuedHoldPresenceProbe(IHoldPresenceProbe inProcess, RecognitionCaller caller, IImageRecognitionGate? gate = null)
    : IHoldPresenceProbe, IAsyncHoldPresenceProbe
{
    /// <inheritdoc/>
    public IReadOnlyList<double?> Score(byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries) =>
        inProcess.Score(oldImage, newImage, queries);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<double?>> ScoreAsync(
        string operation, byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries, CancellationToken ct)
    {
        var parts = new[]
        {
            RecognitionParts.Request(new { version = 1, queries }),
            RecognitionParts.Image("old", "old.jpg", oldImage),
            RecognitionParts.Image("new", "new.jpg", newImage),
        };
        var memory = RecognitionParts.MemoryMb([oldImage, newImage], together: true);
        return await caller.RunAsync(RecognitionKinds.Presence, parts, memory, RecognitionResults.Presence, ct)
            ?? await gate.RecogniseAsync(operation, () => inProcess.Score(oldImage, newImage, queries), ct);
    }
}
