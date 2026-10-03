// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Jobs;

/// <summary>
/// The median duration of each (kind, stage) over recent jobs that ended well: the fallback of the remaining time where a
/// job reports no rate. Read with bounded queries (the newest <see cref="Samples"/> rows of each source), across all walls
/// (durations are no wall's data, and one wall alone rarely has enough).
/// </summary>
public sealed class JobStageHistory
{
    /// <summary>How many recent rows of each source are sampled.</summary>
    public const int Samples = 60;

    private readonly Dictionary<(string Kind, string Stage), TimeSpan> medians;

    /// <summary>Initializes a new instance of the <see cref="JobStageHistory"/> class over the given durations.</summary>
    public JobStageHistory(IEnumerable<(string Kind, string Stage, TimeSpan Duration)> durations)
    {
        medians = durations
            .GroupBy(d => (d.Kind, d.Stage))
            .Select(g => (g.Key, Median: JobEta.Median(g.Select(d => d.Duration))))
            .Where(x => x.Median is not null)
            .ToDictionary(x => x.Key, x => x.Median!.Value);
    }

    /// <summary>No history at all.</summary>
    public static JobStageHistory Empty { get; } = new([]);

    /// <summary>The median duration of the stage, or null with too few samples.</summary>
    public TimeSpan? Median(string kind, string stage) => medians.TryGetValue((kind, stage), out var m) ? m : null;

    /// <summary>The training stage key of a quality (training time depends on it more than on anything else).</summary>
    public static string TrainingStage(SplatQuality quality) => $"training:{quality.ToString().ToLowerInvariant()}";

    /// <summary>Reads the recent durations of every kind.</summary>
    public static async Task<JobStageHistory> LoadAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var durations = new List<(string Kind, string Stage, TimeSpan Duration)>();
        var captures = await db.WallCaptures.AsNoTracking()
            .Where(c => c.TimelineJson != null || c.FollowUpJson != null)
            .OrderByDescending(c => c.CreatedAt)
            .Take(Samples)
            .Select(c => new { c.TimelineJson, c.FollowUpJson })
            .ToListAsync(ct);
        foreach (var capture in captures)
        {
            durations.AddRange(TimelineDurations(capture.TimelineJson));
            durations.AddRange(FollowUpDurations(capture.FollowUpJson));
        }

        var jobs = await db.GpuJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Succeeded && j.CompletedAt != null)
            .OrderByDescending(j => j.CompletedAt)
            .Take(Samples)
            .Select(j => new { j.Quality, j.ClaimedAt, j.CompletedAt, j.InstalledAt })
            .ToListAsync(ct);
        foreach (var job in jobs)
        {
            if (job.ClaimedAt is { } claimed)
            {
                durations.Add((JobKinds.GpuTraining, TrainingStage(job.Quality), job.CompletedAt!.Value - claimed));
            }

            if (job.InstalledAt is { } installed)
            {
                durations.Add((JobKinds.Finish, "finishing", installed - job.CompletedAt!.Value));
            }
        }

        return new JobStageHistory(durations);
    }

    /// <summary>The well-ended stages of a capture timeline, under the kind each lane is reported as.</summary>
    internal static IEnumerable<(string Kind, string Stage, TimeSpan Duration)> TimelineDurations(string? json) =>
        CaptureTimeline.Parse(json)
            .Where(e => e.Outcome == CaptureTimeline.Done && e.Duration is not null)
            .Select(e => (LaneKind(e.Stage), e.Stage, e.Duration!.Value));

    /// <summary>The follow-up steps that ran (with a recorded start) and did not fail.</summary>
    internal static IEnumerable<(string Kind, string Stage, TimeSpan Duration)> FollowUpDurations(string? json) =>
        CaptureFollowUpRecord.Parse(json).Steps
            .Where(s => s.StartedAt is not null && s.Outcome != CaptureFollowUpOutcome.Failed)
            .Select(s => (JobKinds.FollowUp, s.Key, s.At - s.StartedAt!.Value));

    /// <summary>The job kind a timeline lane is reported as.</summary>
    internal static string LaneKind(string stage) => stage switch
    {
        CaptureTimeline.Rerender => JobKinds.TextureRerender,
        CaptureTimeline.Resolve => JobKinds.Resolve,
        _ => JobKinds.Capture,
    };
}
