// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Pulse;

/// <summary>
/// The job rows whose tracked saves pulse, and the pulse each one gives. All of them are workflow state of wall admins
/// (captures, runner jobs, enhancement runs, refreshes, update sessions, staged or unpublished content), so every pulse
/// goes to <see cref="Enums.FeedAudience.Setters"/>.
/// </summary>
public static class WallPulseRows
{
    /// <summary>The pulses of the added and modified job rows in <paramref name="tracker"/>, in tracker order.</summary>
    public static List<WallPulse> Read(ChangeTracker tracker)
    {
        var pulses = new List<WallPulse>();
        var auto = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false; // detect on these rows only: a save of thousands of holds is not scanned again
        try
        {
            Add<WallCapture>(tracker, pulses, c => new WallPulse(c.WallId, PulseTopics.Capture, c.Id, Progress: c.Progress));
            Add<GpuJob>(tracker, pulses, j => new WallPulse(j.WallId, PulseTopics.GpuJob, j.Id, Progress: j.Progress, Ended: IsOver(j.Status)));
            Add<WallEnhancementRun>(tracker, pulses, r => new WallPulse(r.WallId, PulseTopics.Enhancement, r.Id));
            Add<WallRefresh>(tracker, pulses, r => new WallPulse(r.WallId, PulseTopics.Refresh, r.Id));
            Add<WallUpdateSession>(tracker, pulses, s => new WallPulse(s.WallId, PulseTopics.Update, s.Id, Progress: ShapeProgress(s)));
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = auto;
        }

        return pulses;
    }

    /// <summary>Whether a runner job is over for good (a requeue makes it <see cref="GpuJobStatus.Queued"/> again).</summary>
    public static bool IsOver(GpuJobStatus status) => status is GpuJobStatus.Succeeded or GpuJobStatus.Failed or GpuJobStatus.Cancelled;

    private static double? ShapeProgress(WallUpdateSession s) => s.ShapeTotal > 0 ? (double)s.ShapeDone / s.ShapeTotal : null;

    private static void Add<T>(ChangeTracker tracker, List<WallPulse> pulses, Func<T, WallPulse> pulse)
        where T : class
    {
        foreach (var entry in tracker.Entries<T>())
        {
            entry.DetectChanges();
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                pulses.Add(pulse(entry.Entity));
            }
        }
    }
}
