// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Recognition.Worker.Envelope;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary><c>match-panels</c>: <see cref="IHoldOverlapMatcher.Match"/> on parts <c>left</c> and <c>right</c>.</summary>
public sealed class MatchPanelsHandler(IHoldOverlapMatcher matcher) : IRecognitionJobHandler
{
    public string Kind => "match-panels";

    public Task<object> RunAsync(RecognitionEnvelope envelope, CancellationToken ct)
    {
        var request = envelope.Request<MatchRequest>();
        var left = envelope.Image("left");
        var right = envelope.Image("right");
        ct.ThrowIfCancellationRequested();
        var result = matcher.Match(
            left,
            request.LeftHolds.Select(h => h.ToHold()).ToList(),
            right,
            request.RightHolds.Select(h => h.ToHold()).ToList(),
            request.Direction,
            seed: request.Seed?.ToSeed());
        return Task.FromResult(ToResult(result));
    }

    /// <summary>Contracts <c>jobs/recognition/match-result</c>: points as <c>[x, y]</c>.</summary>
    public static object ToResult(HoldOverlapResult r) => new
    {
        version = 1,
        proposals = r.Proposals.Select(p => new
        {
            leftHoldId = p.LeftHoldId,
            rightHoldId = p.RightHoldId,
            confidence = p.Confidence,
            moved = p.Moved,
            residualPx = p.ResidualPx,
            rescue = p.Rescue,
        }),
        unmatchedLeft = r.UnmatchedLeft,
        unmatchedRight = r.UnmatchedRight,
        warpedLeftPositions = r.WarpedLeftPositions?.Select(p => p is { } q ? new[] { q.X, q.Y } : null),
        warpedLeftShapes = r.WarpedLeftShapes?.Select(s => s?.Select(q => new[] { q.X, q.Y })),
    };
}
