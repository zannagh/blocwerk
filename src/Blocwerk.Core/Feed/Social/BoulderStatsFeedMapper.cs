// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// Ticks, ratings and grade proposals: one coarse, actor-less <c>boulder.stats</c> per boulder and save for the wall
/// (S2; the counts are computed at read time), plus the person's own <c>tick.mine</c> / <c>rating.mine</c> for their
/// devices only. No stars, grades or notes are stored. Moving an ascent between training activities changes no count
/// and maps to nothing.
/// </summary>
internal sealed class BoulderStatsFeedMapper : IFeedMapper
{
    private static readonly IReadOnlySet<string> AttemptVisible = new HashSet<string>
    {
        nameof(Attempt.Type), nameof(Attempt.Timestamp), nameof(Attempt.Notes), nameof(Attempt.BoulderId), nameof(Attempt.UserId),
    };

    private static readonly IReadOnlySet<string> RatingVisible = new HashSet<string>
    {
        nameof(BoulderRating.Stars), nameof(BoulderRating.BoulderId), nameof(BoulderRating.UserId),
    };

    private static readonly IReadOnlySet<string> ProposalVisible = new HashSet<string>
    {
        nameof(GradeProposal.ProposedGrade), nameof(GradeProposal.IsResolved), nameof(GradeProposal.BoulderId),
    };

    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(Attempt), typeof(BoulderRating), typeof(GradeProposal)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        switch (entry.Entity)
        {
            case Attempt attempt when SocialRows.Changed(entry, AttemptVisible):
                SocialRows.AddStats(context, attempt.BoulderId);
                SocialRows.AddMine(context, FeedKinds.TickMine, attempt.UserId, attempt.BoulderId);
                break;
            case BoulderRating rating when SocialRows.Changed(entry, RatingVisible):
                SocialRows.AddStats(context, rating.BoulderId);
                SocialRows.AddMine(context, FeedKinds.RatingMine, rating.UserId, rating.BoulderId);
                break;
            case GradeProposal proposal when SocialRows.Changed(entry, ProposalVisible):
                SocialRows.AddStats(context, proposal.BoulderId);
                break;
        }
    }
}
