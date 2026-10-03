// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Capture;

/// <summary>One save's change to a capture that the timeline records (status, re-render mark, re-solve mark).</summary>
/// <param name="CaptureId">The capture.</param>
/// <param name="Added">The row was inserted by the save.</param>
/// <param name="From">The status before the save (null: inserted).</param>
/// <param name="To">The status after it.</param>
/// <param name="TexturesBefore">The textures job id before (the re-render mark).</param>
/// <param name="TexturesAfter">The textures job id after.</param>
/// <param name="SolveBefore">The solve job id before (the re-solve mark).</param>
/// <param name="SolveAfter">The solve job id after.</param>
/// <param name="At">When it was saved.</param>
public sealed record CaptureTimelineChange(
    Guid CaptureId,
    bool Added,
    WallCaptureStatus? From,
    WallCaptureStatus To,
    string? TexturesBefore,
    string? TexturesAfter,
    string? SolveBefore,
    string? SolveAfter,
    DateTimeOffset At);

/// <summary>
/// Reading the timeline's changes from a save. The status and the job marks are written with tracked saves (the follow-up
/// record is not: <c>CaptureFollowUpRecordStore</c> writes it conditionally and stamps <see cref="WallCapture.UpdatedAt"/>
/// itself), so they are compared with their loaded values here, in one place, instead of at each pipeline call site.
/// </summary>
public static partial class CaptureTimeline
{
    /// <summary>
    /// The timeline changes of every added or modified capture in <paramref name="tracker"/>, and
    /// <see cref="WallCapture.UpdatedAt"/> stamped on each. Detects changes on the captures only, so a save of thousands of
    /// holds is not scanned twice. Pure with respect to the stored timeline: a retried save computes the same changes again.
    /// </summary>
    internal static List<CaptureTimelineChange> Changes(ChangeTracker tracker, DateTimeOffset now)
    {
        var changes = new List<CaptureTimelineChange>();
        var auto = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in tracker.Entries<WallCapture>())
            {
                entry.DetectChanges();
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    changes.Add(Change(entry, now));
                    entry.Entity.UpdatedAt = now;
                }
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = auto;
        }

        return changes;
    }

    /// <summary>Applies one change onto the timeline as stored now; false when nothing changes.</summary>
    internal static bool Apply(List<CaptureTimelineEntry> entries, CaptureTimelineChange change)
    {
        var changed = false;
        if (change.From != change.To)
        {
            changed |= OnStatus(entries, change.From, change.To, change.At);
        }

        changed |= OnLane(
            entries, Rerender, CaptureTextureOutcome.IsRerendering(change.TexturesBefore), CaptureTextureOutcome.IsRerendering(change.TexturesAfter),
            change.TexturesAfter is not null, change.At);
        changed |= OnLane(
            entries, Resolve, CaptureResolveMark.IsResolving(change.SolveBefore), CaptureResolveMark.IsResolving(change.SolveAfter),
            change.SolveAfter is not null, change.At);
        return changed;
    }

    private static CaptureTimelineChange Change(EntityEntry<WallCapture> entry, DateTimeOffset now)
    {
        var c = entry.Entity;
        var added = entry.State == EntityState.Added;
        return new CaptureTimelineChange(
            c.Id,
            added,
            added ? null : entry.Property(x => x.Status).OriginalValue,
            c.Status,
            added ? null : entry.Property(x => x.TexturesJobId).OriginalValue,
            c.TexturesJobId,
            added ? null : entry.Property(x => x.SolveJobId).OriginalValue,
            c.SolveJobId,
            now);
    }
}
