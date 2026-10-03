// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Stamping the timeline from what a save changes: every capture write goes through a tracked save, so the status and the
/// job marks are compared with their loaded values here, in one place, instead of at each of the pipeline's call sites.
/// </summary>
public static partial class CaptureTimeline
{
    /// <summary>
    /// Updates <see cref="WallCapture.TimelineJson"/> and <see cref="WallCapture.UpdatedAt"/> of every added or modified
    /// capture in <paramref name="tracker"/>. Detects changes on the captures only, so a save of thousands of holds is
    /// not scanned twice.
    /// </summary>
    internal static void Stamp(ChangeTracker tracker, DateTimeOffset now)
    {
        var auto = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in tracker.Entries<WallCapture>())
            {
                entry.DetectChanges();
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    Stamp(entry, now);
                }
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = auto;
        }
    }

    private static void Stamp(EntityEntry<WallCapture> entry, DateTimeOffset now)
    {
        var capture = entry.Entity;
        var added = entry.State == EntityState.Added;
        var entries = Parse(capture.TimelineJson);
        var changed = false;

        var from = added ? (WallCaptureStatus?)null : entry.Property(c => c.Status).OriginalValue;
        if (from != capture.Status)
        {
            changed |= OnStatus(entries, from, capture.Status, now);
        }

        var textures = Original(entry, added, c => c.TexturesJobId);
        changed |= OnLane(
            entries, Rerender, CaptureTextureOutcome.IsRerendering(textures), CaptureTextureOutcome.IsRerendering(capture.TexturesJobId),
            capture.TexturesJobId is not null, now);
        var solve = Original(entry, added, c => c.SolveJobId);
        changed |= OnLane(
            entries, Resolve, CaptureResolveMark.IsResolving(solve), CaptureResolveMark.IsResolving(capture.SolveJobId),
            capture.SolveJobId is not null, now);

        if (changed)
        {
            capture.TimelineJson = ToJson(entries);
        }

        // Every write of the row counts: the follow-up chain and the side lanes write only their JSON and job marks.
        capture.UpdatedAt = now;
    }

    private static string? Original(EntityEntry<WallCapture> entry, bool added, System.Linq.Expressions.Expression<Func<WallCapture, string?>> property) =>
        added ? null : entry.Property(property).OriginalValue;
}
