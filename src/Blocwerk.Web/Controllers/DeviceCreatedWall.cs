// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>The wall <c>POST /api/v1/walls</c> made (or, for a retry, made before).</summary>
/// <param name="WallId">The new wall; the caller owns it and is its admin.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its description, or null.</param>
/// <param name="Angle">Its angle.</param>
public sealed record DeviceCreatedWall(Guid WallId, string Name, string? Description, int Angle);
