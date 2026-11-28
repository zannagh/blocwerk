// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>One row of a boulder's history as the web boulder page lists it: who logged what, and when.</summary>
/// <param name="Id">The attempt's id.</param>
/// <param name="UserId">The climber.</param>
/// <param name="UserName">The climber's display name, as the web shows it.</param>
/// <param name="Type">"Attempt", "Send" or "Flash".</param>
/// <param name="Timestamp">When it was climbed.</param>
/// <param name="Mine">True for the caller's own row, which the web lets them delete.</param>
public sealed record BoulderAscentResponse(
    Guid Id,
    Guid UserId,
    string UserName,
    string Type,
    DateTimeOffset Timestamp,
    bool Mine);
