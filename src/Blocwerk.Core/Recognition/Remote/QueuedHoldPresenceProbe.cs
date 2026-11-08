// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The presence probe as an interactive <c>presence</c> job; in the app when <see cref="RecognitionCaller"/> says so. The
/// interface is synchronous and its callers run it off the circuit (<see cref="ImageRecognitionGateExtensions.RecogniseAsync"/>),
/// so the wait blocks a pool thread, never a circuit; it runs on the pool itself so no synchronization context is captured.
/// </summary>
public sealed class QueuedHoldPresenceProbe(IHoldPresenceProbe inProcess, RecognitionCaller caller) : IHoldPresenceProbe
{
    /// <inheritdoc/>
    public IReadOnlyList<double?> Score(byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries)
    {
        var parts = new[]
        {
            RecognitionParts.Request(new { version = 1, queries }),
            RecognitionParts.Image("old", "old.jpg", oldImage),
            RecognitionParts.Image("new", "new.jpg", newImage),
        };
        var memory = RecognitionParts.MemoryMb([oldImage, newImage], together: true);
        var scores = Task.Run(() => caller.RunAsync(RecognitionKinds.Presence, parts, memory, RecognitionResults.Presence, CancellationToken.None))
            .GetAwaiter().GetResult();
        return scores ?? inProcess.Score(oldImage, newImage, queries);
    }
}
