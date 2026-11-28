// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Answer of <c>GET /api/v1/walls/{wallId}/hold-links</c>: what the web wall page's "link holds across panels" reminder
/// says. The reminder shows while <see cref="PanelCount"/> is at least 2 and <see cref="LinksFinalized"/> is false.
/// </summary>
/// <param name="PanelCount">The wall's live panels.</param>
/// <param name="LinkCount">Hold pairs linked across them, counted between the live panels' live holds only.</param>
/// <param name="LinksFinalized">True when an editor marked the links finished for the wall's current generation.</param>
public sealed record WallHoldLinksResponse(int PanelCount, int LinkCount, bool LinksFinalized);
