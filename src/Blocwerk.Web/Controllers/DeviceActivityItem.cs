// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>One activity row as the web wall and boulder pages list it.</summary>
/// <param name="Id">The row.</param>
/// <param name="Type">The kind, e.g. BoulderCreated, AttemptLogged, CommentAdded, BetaVideoUploaded, MemberJoined.</param>
/// <param name="UserId">The author, or null when shown as Ghost (an erased account or the system).</param>
/// <param name="UserName">The author's name as the web shows it.</param>
/// <param name="AvatarUrl">The author's avatar, or null.</param>
/// <param name="BoulderId">The boulder it is about, or null for a wall-wide row.</param>
/// <param name="Details">The row's free text, e.g. a boulder name or "12 holds detected".</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="SubjectUserId">The person a member row is about (joined, role changed, removed, left), or null.</param>
/// <param name="SubjectUserName">Their name as the web shows it, or null.</param>
public sealed record DeviceActivityItem(
    Guid Id,
    string Type,
    Guid? UserId,
    string UserName,
    string? AvatarUrl,
    Guid? BoulderId,
    string? Details,
    DateTimeOffset Timestamp,
    Guid? SubjectUserId = null,
    string? SubjectUserName = null);
