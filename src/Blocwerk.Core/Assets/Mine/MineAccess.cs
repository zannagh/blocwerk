// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.Mine;

/// <summary>The outcome of <see cref="MineAccessGate"/>: whose marks to read and in which class, or why none.</summary>
/// <param name="UserId">The signed-in reader, null when refused.</param>
/// <param name="Audience">The reader's class on the wall, null when refused.</param>
/// <param name="Refusal">Why it was refused; <see cref="FeedAccessRefusal.None"/> when granted.</param>
/// <param name="WallMissing">True when the refusal is for a wall that does not exist (the route's 404).</param>
public sealed record MineAccess(Guid? UserId, DocumentAudience? Audience, FeedAccessRefusal Refusal, bool WallMissing = false)
{
    public static MineAccess Grant(Guid userId, DocumentAudience audience) => new(userId, audience, FeedAccessRefusal.None);

    public static MineAccess Refuse(FeedAccessRefusal refusal, bool wallMissing = false) => new(null, null, refusal, wallMissing);
}
