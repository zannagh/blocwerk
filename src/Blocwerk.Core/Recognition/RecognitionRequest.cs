// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// One recognition job to queue: its kind (<see cref="RecognitionKinds"/>), lane and wall, the envelope parts (the
/// kind's <c>request</c> JSON and its photos), the memory it needs on a runner (from the photos' pixel counts) and an
/// optional key the caller finds it again by. <c>Seeds</c> is the work of an <c>outline-holds</c> job; it sizes the run deadline.
/// </summary>
public sealed record RecognitionRequest(
    string Kind,
    RecognitionLane Lane,
    Guid WallId,
    IReadOnlyList<ComputeJobPart> Parts,
    int? RequiredMemoryMb = null,
    string? CallerKey = null,
    int Seeds = 0);
