// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>The answer of <c>PUT /api/v1/walls/{w}/photo</c>.</summary>
/// <param name="PhotoSha">The stored photo's sha (read it as <c>/api/walls/{w}/a/{sha}</c>); null while it is being hashed.</param>
public sealed record DeviceWallPhotoResponse(string? PhotoSha);
