// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>
/// What the web wall page knows about a boulder beyond the <c>boulders</c> document, for its filters, badges and the edit
/// pencil: who created and set it (by id), whether it has beta clips, whether it needs review, and whether the caller
/// may edit it.
/// </summary>
/// <param name="BoulderId">The boulder.</param>
/// <param name="CreatedByUserId">Its creator ("Created by me" compares this with the caller).</param>
/// <param name="SetterIds">Its setters, by user id.</param>
/// <param name="HasBetaVideo">True when it has at least one beta clip (the "Has beta video" filter).</param>
/// <param name="NeedsReview">True while the last wall update left it for review (the badge and the filter).</param>
/// <param name="CanEdit">True for its creator, one of its setters or a wall admin (the web shows the pencil then).</param>
public sealed record DeviceBoulderMeta(
    Guid BoulderId,
    Guid CreatedByUserId,
    IReadOnlyList<Guid> SetterIds,
    bool HasBetaVideo,
    bool NeedsReview,
    bool CanEdit);
