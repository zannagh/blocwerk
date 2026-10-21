// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Pulse;

/// <summary>
/// One ephemeral "this job moved" signal of a wall (design §5.6). It carries ids, a fraction and an end flag, never text
/// or a user id: a page re-reads the job through its own service call, which keeps that call's authorisation.
/// </summary>
/// <param name="WallId">The wall the job works on.</param>
/// <param name="Topic">Which kind of job (<see cref="PulseTopics"/>).</param>
/// <param name="JobId">The job (capture, GPU job, run, refresh, session; the wall for a hold search).</param>
/// <param name="Audience">Who may receive it. <see cref="FeedAudience.User"/> is never delivered: a pulse has no user.</param>
/// <param name="Progress">How far it is (0..1), when the job says.</param>
/// <param name="Ended">The job ended (any outcome). An ended pulse is never held back by the throttle.</param>
public sealed record WallPulse(
    Guid WallId, string Topic, Guid JobId, FeedAudience Audience = FeedAudience.Setters, double? Progress = null, bool Ended = false);
