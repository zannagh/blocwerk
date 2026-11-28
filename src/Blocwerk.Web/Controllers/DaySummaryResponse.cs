// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>Answer of <c>GET /api/v1/me/days/{date}</c>: what the Activity page's day panel shows for one date.</summary>
/// <param name="Date">The day (UTC).</param>
/// <param name="Boulders">The boulders tried that day, with the best result and the number of tries.</param>
/// <param name="Hangboard">The hangboard sessions that day.</param>
/// <param name="Pullups">The pull-up sessions that day.</param>
/// <param name="SessionDurationMinutes">How long the day's climbing lasted, or null without any.</param>
public sealed record DaySummaryResponse(
    DateOnly Date,
    IReadOnlyList<BoulderAttemptSummaryResponse> Boulders,
    IReadOnlyList<HangboardSessionResponse> Hangboard,
    IReadOnlyList<PullupSessionResponse> Pullups,
    int? SessionDurationMinutes);
