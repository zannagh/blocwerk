// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The overlap pass between two panels as an interactive <c>match-panels</c> job; in the app when
/// <see cref="RecognitionCaller"/> says so. The carryover pass (<see cref="IHoldCarryMatcher"/>) has no job kind yet and
/// stays in the app. The diagnostics sink is not sent: it only adds a log line. Synchronous like
/// <see cref="QueuedHoldPresenceProbe"/>, for the same reason.
/// </summary>
public sealed class QueuedHoldOverlapMatcher(IHoldOverlapMatcher inProcess, RecognitionCaller caller) : IHoldOverlapMatcher, IHoldCarryMatcher
{
    /// <inheritdoc/>
    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null,
        HoldOverlapSeed? seed = null)
    {
        var parts = new[]
        {
            RecognitionParts.Request(RecognitionMatchRequest.Of(leftHolds, rightHolds, direction, seed)),
            RecognitionParts.Image("left", "left.jpg", leftImage),
            RecognitionParts.Image("right", "right.jpg", rightImage),
        };
        var memory = RecognitionParts.MemoryMb([leftImage, rightImage], together: true);
        var result = Task.Run(() => caller.RunAsync(RecognitionKinds.MatchPanels, parts, memory, RecognitionResults.Match, CancellationToken.None))
            .GetAwaiter().GetResult();
        return result ?? inProcess.Match(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed);
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
