// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Pulse;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Job progress for the pages (<see cref="IWallPulse"/>): the queue writes its status, heartbeat and stage with conditional
/// bulk updates, which the pulse interceptor never sees, so each such write that took effect pulses here.
/// </summary>
public sealed partial class GpuJobQueue
{
    private void PulseJob(Guid wallId, Guid jobId, double? progress, bool ended = false) =>
        pulse?.Publish(new WallPulse(wallId, PulseTopics.GpuJob, jobId, FeedAudience.Setters, progress, ended));
}
