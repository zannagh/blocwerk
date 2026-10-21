// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Linq.Expressions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Which rows a read returns. The caller has already checked that the viewer may read the wall; the filter only narrows
/// the rows to the audiences the viewer belongs to. <see cref="FeedAudience.User"/> rows go to their own user only.
/// </summary>
public sealed class FeedFilter
{
    private FeedFilter(Expression<Func<WallChange, bool>> predicate)
    {
        Predicate = predicate;
    }

    /// <summary>Gets the row predicate (translates to SQL on both providers).</summary>
    public Expression<Func<WallChange, bool>> Predicate { get; }

    /// <summary>One wall's rows for the given audiences, plus that wall's <see cref="FeedAudience.User"/> rows of <paramref name="viewerUserId"/>.</summary>
    public static FeedFilter ForWall(Guid wallId, IReadOnlyCollection<FeedAudience> audiences, Guid? viewerUserId)
    {
        var shared = audiences.Where(a => a != FeedAudience.User).ToArray();
        return new FeedFilter(c => c.WallId == wallId
            && (shared.Contains(c.Audience) || (c.Audience == FeedAudience.User && viewerUserId != null && c.AudienceUserId == viewerUserId)));
    }

    /// <summary>One user's own rows across every wall (favourites, own ticks, profile).</summary>
    public static FeedFilter ForUser(Guid userId) =>
        new(c => c.Audience == FeedAudience.User && c.AudienceUserId == userId);
}
