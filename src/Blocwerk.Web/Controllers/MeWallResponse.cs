// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>One card of the caller's wall list (<c>GET /api/v1/me/walls</c>), as the web My Walls page shows it.</summary>
/// <param name="Id">The wall's id.</param>
/// <param name="Name">The wall's name.</param>
/// <param name="MemberCount">How many members the wall has.</param>
/// <param name="HasPhoto">True when the wall has a photo (the card shows "No Photo" otherwise).</param>
/// <param name="PhotoSha">
/// The card photo's hash, read as <c>/api/walls/{id}/a/{sha}/640</c>; null without a photo or while it is not hashed yet.
/// </param>
public sealed record MeWallResponse(Guid Id, string Name, int MemberCount, bool HasPhoto, string? PhotoSha);
