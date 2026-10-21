// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>
/// <c>wall.updated</c>: the live generation, and whether the owner changed. A flag, not the owner's id: the row reaches
/// share and kiosk viewers, who get no user ids. Circuits read their own role again only when it is set.
/// </summary>
/// <param name="Generation">The wall's live generation.</param>
/// <param name="OwnerChanged">True when this save moved the wall to another owner.</param>
public sealed record WallPayload(int Generation, bool OwnerChanged) : FeedPayload;
