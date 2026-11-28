// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>Answer of <c>POST /api/v1/join/{token}</c>: the wall the caller is now a member of.</summary>
/// <param name="WallId">The wall's id.</param>
/// <param name="Name">The wall's name.</param>
public sealed record JoinedWallResponse(Guid WallId, string Name);
