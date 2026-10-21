// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>One save's queued comment compaction (<see cref="FeedCommentCompaction"/>).</summary>
internal sealed class FeedCompactionQueue
{
    /// <summary>Gets the deleted comments whose <c>comment.added</c> rows go.</summary>
    public List<Guid> Comments { get; } = [];

    /// <summary>Gets the deleted boulders, all of whose <c>comment.added</c> rows go (their comments went by cascade).</summary>
    public List<Guid> Boulders { get; } = [];
}
