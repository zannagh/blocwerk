// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>Whom an open stream counts against in <see cref="FeedStreamLimits"/>.</summary>
/// <param name="UserId">The signed-in user (a non-member reading through a share token too), or null for nobody and a kiosk.</param>
/// <param name="WallId">The wall streamed.</param>
/// <param name="Address">The caller's address partition (<see cref="FeedAddress"/>).</param>
public sealed record FeedStreamClaim(Guid? UserId, Guid WallId, string Address);
