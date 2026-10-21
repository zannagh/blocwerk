// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// When the hub reads a wall again (design §5.2): a woken transaction that is not yet below the horizon (an older one is
/// still open) is re-polled at 250 ms, 1 s, 2 s and then every 5 s; every wall is read at least every 30 s regardless, for
/// wakes that never came (a crash between commit and wake, a dropped wake).
/// </summary>
public static class FeedRepoll
{
    /// <summary>The safety poll of every subscribed wall.</summary>
    public static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] Steps =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>The wait before re-poll number <paramref name="attempt"/> (1-based).</summary>
    public static TimeSpan Delay(int attempt) => Steps[Math.Clamp(attempt, 1, Steps.Length) - 1];
}
