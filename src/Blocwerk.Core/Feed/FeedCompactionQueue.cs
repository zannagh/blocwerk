// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>One save's queued comment compaction (<see cref="FeedCommentCompaction"/>).</summary>
internal sealed class FeedCompactionQueue
{
    /// <summary>Gets the deleted comments with their walls, whose <c>comment.added</c> rows go.</summary>
    public List<(Guid WallId, Guid CommentId)> Comments { get; } = [];

    /// <summary>Gets the deleted boulders with their walls; all their <c>comment.added</c> rows go (the comments went by cascade).</summary>
    public List<(Guid WallId, Guid BoulderId)> Boulders { get; } = [];
}
