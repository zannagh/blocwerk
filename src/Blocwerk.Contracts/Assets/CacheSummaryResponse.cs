// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>The body of <c>GET /api/me/cache-summary</c>: what the client's storage notices need.</summary>
/// <param name="WallsWith3d">
/// How many of the viewer's walls have 3D data a manifest lists for them (the high-storage banner shows from 3). Counted
/// over the walls the viewer may fetch: their walls as a member, the bound wall for a kiosk tablet that may cache it, none
/// for an anonymous viewer (a share link is not membership).
/// </param>
/// <param name="Walls">
/// The ids of those same walls: the ones the client keeps on the device whether or not they were opened (the page's fill
/// queue walks them). A share-linked wall is not listed; the page caches it only while it is open.
/// </param>
public sealed record CacheSummaryResponse(int WallsWith3d, IReadOnlyList<Guid> Walls);
