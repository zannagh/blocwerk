// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Commit observers for tests that do not watch the own-write path.</summary>
internal static class TestFeedCommits
{
    public static FeedCommits Unobserved() => new(NullLogger<FeedCommits>.Instance);
}
