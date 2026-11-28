// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>One row of the web wall page's leaderboard card.</summary>
/// <param name="UserId">The member.</param>
/// <param name="Name">Their display name.</param>
/// <param name="AvatarUrl">Their avatar, or null.</param>
/// <param name="HardestSendGrade">Their hardest send on the wall, or null without a send.</param>
/// <param name="HardestSendScore">That send's score (flash bonus included), for sorting.</param>
/// <param name="Score">The wall-scoped rolling boulder rating.</param>
/// <param name="VolumeMinutes">All-time training minutes on the wall.</param>
public sealed record DeviceLeaderboardEntry(
    Guid UserId,
    string Name,
    string? AvatarUrl,
    string? HardestSendGrade,
    int HardestSendScore,
    int Score,
    int VolumeMinutes);
