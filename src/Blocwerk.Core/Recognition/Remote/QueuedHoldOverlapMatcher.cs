// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The overlap pass between two panels as an interactive <c>match-panels</c> job (<see cref="IAsyncHoldOverlapMatcher"/>),
/// awaited without a gate slot; only the in-app run <see cref="RecognitionCaller"/> falls back to takes one. The carryover
/// pass (<see cref="IHoldCarryMatcher"/>) has no job kind yet and stays in the app, and so does the synchronous
/// <see cref="Match"/>, for the reason <see cref="QueuedHoldPresenceProbe"/> gives. The diagnostics sink is not sent: it
/// only adds a log line.
/// </summary>
public sealed class QueuedHoldOverlapMatcher(IHoldOverlapMatcher inProcess, RecognitionCaller caller, IImageRecognitionGate? gate = null)
    : IHoldOverlapMatcher, IHoldCarryMatcher, IAsyncHoldOverlapMatcher
{
    /// <inheritdoc/>
    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null) =>
        inProcess.Match(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed);

    /// <inheritdoc/>
    public async Task<HoldOverlapResult> MatchAsync(
        string operation, MatcherPair pair, HoldOverlapDirection direction, ILogger? diag, HoldOverlapSeed? seed, CancellationToken ct)
    {
        var parts = new[]
        {
            RecognitionParts.Request(RecognitionMatchRequest.Of(pair.LeftHolds, pair.RightHolds, direction, seed)),
            RecognitionParts.Image("left", "left.jpg", pair.LeftImage),
            RecognitionParts.Image("right", "right.jpg", pair.RightImage),
        };
        var memory = RecognitionParts.MemoryMb([pair.LeftImage, pair.RightImage], together: true);
        return await caller.RunAsync(RecognitionKinds.MatchPanels, parts, memory, RecognitionResults.Match, ct)
            ?? await gate.RecogniseAsync(
                operation, () => inProcess.Match(pair.LeftImage, pair.LeftHolds, pair.RightImage, pair.RightHolds, direction, diag, seed), ct);
    }

    /// <inheritdoc/>
    public HoldOverlapResult MatchCarry(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null) =>
        inProcess is IHoldCarryMatcher carry
            ? carry.MatchCarry(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed)
            : inProcess.Match(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed);
}
