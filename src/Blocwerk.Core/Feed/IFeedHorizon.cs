// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The provider's notion of "every row up to here is final". A read takes the <see cref="CeilingAsync"/> first and then
/// returns only rows at or below it, so a row that commits between the two statements is never stepped over: it lies above
/// the ceiling and is served by a later read.
/// </summary>
public interface IFeedHorizon
{
    /// <summary>
    /// The newest position no future commit can land at or below. Doubles as the <c>CursorAtLoad</c>: every transaction at
    /// or below it has ended, so a query run after this call sees all of them.
    /// </summary>
    Task<FeedPosition> CeilingAsync(BlocwerkDbContext db, CancellationToken cancellationToken);

    /// <summary>Narrows <paramref name="source"/> to the rows after <paramref name="after"/> and at or below <paramref name="ceiling"/>.</summary>
    IQueryable<WallChange> Between(IQueryable<WallChange> source, FeedPosition after, FeedPosition ceiling);

    /// <summary>Orders rows in feed order.</summary>
    IQueryable<WallChange> InOrder(IQueryable<WallChange> source);
}
