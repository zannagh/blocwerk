// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>The outcome of <see cref="DocumentAudienceGate"/>: the caller's class, or why there is none.</summary>
/// <param name="Audience">The class, null when refused.</param>
/// <param name="Refusal">Why it was refused; <see cref="FeedAccessRefusal.None"/> when granted.</param>
/// <param name="WallMissing">True when the refusal is for a wall that does not exist (the index's 404).</param>
public sealed record DocumentAccess(DocumentAudience? Audience, FeedAccessRefusal Refusal, bool WallMissing = false)
{
    public static DocumentAccess Grant(DocumentAudience audience) => new(audience, FeedAccessRefusal.None);

    public static DocumentAccess Refuse(FeedAccessRefusal refusal, bool wallMissing = false) => new(null, refusal, wallMissing);
}
