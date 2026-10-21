// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>A <see cref="FeedCommits"/> that records every committed row it hands out, in order.</summary>
internal sealed class RecordingFeedCommits
{
    private readonly List<FeedCommit> commits = [];

    public RecordingFeedCommits()
    {
        _ = Commits.Observe(commit =>
        {
            lock (commits)
            {
                commits.Add(commit);
            }
        });
    }

    public FeedCommits Commits { get; } = new(NullLogger<FeedCommits>.Instance);

    public IReadOnlyList<FeedCommit> Batches
    {
        get
        {
            lock (commits)
            {
                return commits.ToList();
            }
        }
    }

    public IReadOnlyList<WallChange> Rows => Batches.SelectMany(c => c.Rows).ToList();

    public void Clear()
    {
        lock (commits)
        {
            commits.Clear();
        }
    }
}
