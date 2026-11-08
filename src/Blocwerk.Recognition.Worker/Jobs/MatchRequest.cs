// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>Contracts <c>jobs/recognition/match-request</c>: the arguments of <see cref="IHoldOverlapMatcher.Match"/> besides the images.</summary>
public sealed record MatchRequest(
    IReadOnlyList<MatchRequestHold> LeftHolds,
    IReadOnlyList<MatchRequestHold> RightHolds,
    HoldOverlapDirection Direction = HoldOverlapDirection.Right,
    MatchRequestSeed? Seed = null);
