// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>Answer of <c>GET /api/v1/join/{token}</c>: the wall an invite token leads to, for "Join &lt;wall&gt;?".</summary>
/// <param name="WallId">The wall's id.</param>
/// <param name="Name">The wall's name.</param>
/// <param name="MemberCount">How many members it has.</param>
public sealed record JoinPreviewResponse(Guid WallId, string Name, int MemberCount);
