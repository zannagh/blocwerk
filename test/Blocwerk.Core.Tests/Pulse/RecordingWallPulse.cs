// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Blocwerk.Core.Pulse;

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>An <see cref="IWallPulse"/> that records every publish, unthrottled, for the job tests.</summary>
public sealed class RecordingWallPulse : IWallPulse
{
    private readonly List<WallPulse> published = [];

    public IReadOnlyList<WallPulse> Published
    {
        get
        {
            lock (published)
            {
                return [.. published];
            }
        }
    }

    public void Publish(WallPulse pulse)
    {
        lock (published)
        {
            published.Add(pulse);
        }
    }

    public IDisposable Subscribe(Guid wallId, Func<FeedViewer?> viewer, Action<WallPulse> onPulse) => new CancellationTokenSource();

    public IReadOnlyList<WallPulse> Of(string topic) => Published.Where(p => p.Topic == topic).ToList();
}
