// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State.Feed;

/// <summary>What one feed event removed from a circuit's cache.</summary>
/// <param name="Wall">True when wall-level entries went (the aggregate, a hold set, or all of the wall).</param>
/// <param name="Boulders">The cached boulders that went.</param>
public sealed record WallFeedEvicted(bool Wall, IReadOnlyCollection<Guid> Boulders)
{
    public static WallFeedEvicted None { get; } = new(false, []);

    /// <summary>
    /// Gets the boulders evicted while their load was in flight: each resolves to the boulder's id once it loaded, if it
    /// is of the event's wall, else to null. Their wall is unknown until then, so they are reported only after.
    /// </summary>
    public IReadOnlyCollection<Task<Guid?>> InFlight { get; init; } = [];

    public bool Any => Wall || Boulders.Count > 0;

    public static WallFeedEvicted Whole(bool any) => any ? new(true, []) : None;

    /// <summary>Calls <paramref name="report"/> with each in-flight boulder that turned out to be of the event's wall.</summary>
    public static void WhenLoaded(IEnumerable<Task<Guid?>> inFlight, Action<Guid> report)
    {
        foreach (var load in inFlight)
        {
            _ = load.ContinueWith(
                t =>
                {
                    if (t.Result is { } id)
                    {
                        report(id);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }
    }
}
