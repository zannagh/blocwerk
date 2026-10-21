// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Pulse;

/// <summary>
/// Ephemeral job progress per wall (design §5.6): in memory, never persisted, latest value wins. A sibling of the durable
/// feed, not part of it: a lost pulse costs a page one safety-poll interval, nothing more.
/// <para>
/// <b>Throttle.</b> At most one pulse per (wall, topic, job) per <see cref="WallPulseHub.Interval"/> reaches subscribers;
/// the ones in between collapse into the latest, delivered when the interval ends. An ended pulse goes at once.
/// </para>
/// <para>
/// <b>Delivery.</b> One background loop, in publish order per job. A handler must not block (hand UI work off with an
/// unawaited <c>InvokeAsync</c>) and must tolerate one more call after its subscription is disposed.
/// </para>
/// </summary>
public interface IWallPulse
{
    /// <summary>Publishes a pulse. Never blocks and never throws, so a job can call it from anywhere.</summary>
    void Publish(WallPulse pulse);

    /// <summary>
    /// Subscribes to <paramref name="wallId"/>'s pulses. <paramref name="viewer"/> is read at every delivery: a pulse
    /// reaches the handler only when it returns a viewer whose audiences include the pulse's. The caller has checked that
    /// the viewer may read the wall.
    /// </summary>
    IDisposable Subscribe(Guid wallId, Func<FeedViewer?> viewer, Action<WallPulse> onPulse);
}
