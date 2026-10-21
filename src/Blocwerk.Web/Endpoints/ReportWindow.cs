// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// One user's report times in a sliding window. Every read and write happens under a lock on the instance;
/// <see cref="Retired"/> is set (under that lock) when the window is pruned from its map, so an admission
/// that fetched it just before the prune sees the flag and starts over on a fresh window instead of counting
/// into one nobody will ever look at again.
/// </summary>
internal sealed class ReportWindow
{
    public Queue<DateTimeOffset> Times { get; } = new();

    public bool Retired { get; set; }

    /// <summary>True when nothing in the window is younger than <paramref name="window"/>.</summary>
    public bool IsIdle(DateTimeOffset now, TimeSpan window) => Times.Count == 0 || now - Times.Last() > window;
}
