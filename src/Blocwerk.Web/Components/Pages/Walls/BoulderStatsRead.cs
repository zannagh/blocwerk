// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// One live stats refresh of the boulder page (BoulderDetail.Feed.cs), read first and applied in one step, so nothing of
/// it lands when the page has moved on to another boulder meanwhile. A null part was not re-read.
/// </summary>
internal sealed class BoulderStatsRead
{
    /// <summary>Gets or sets a value indicating whether the open grade proposal was re-read (it may be null).</summary>
    public bool ProposalRead { get; set; }

    public GradeProposal? Proposal { get; set; }

    public List<Attempt>? Attempts { get; set; }

    public AttemptSummary? Summary { get; set; }

    public RatingInfo? Rating { get; set; }

    public bool? Favorite { get; set; }
}
