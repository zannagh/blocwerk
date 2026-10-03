// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Jobs;

/// <summary>
/// Captures: the pipeline (status, progress, stage text, timeline), and from the same row the side lanes (texture re-render,
/// re-solve) and the follow-up chain (<c>JobProgressReader.FollowUps.cs</c>).
/// </summary>
public sealed partial class JobProgressReader
{
    private static readonly WallCaptureStatus[] RunningStatuses =
    [
        WallCaptureStatus.Queued, WallCaptureStatus.Detecting, WallCaptureStatus.Solving, WallCaptureStatus.Texturing,
        WallCaptureStatus.Splatting,
    ];

    private static async Task<List<JobProgressItem>> CaptureItemsAsync(BlocwerkDbContext db, JobProgressReadContext context, CancellationToken ct)
    {
        var rows = await JobCaptureRowsAsync(db, context, ct);
        var items = new List<JobProgressItem>();
        foreach (var row in rows)
        {
            var timeline = CaptureTimeline.Parse(row.TimelineJson);
            if (CaptureItem(row, timeline, context) is { } capture)
            {
                items.Add(capture);
            }

            items.AddRange(LaneItems(row, timeline, context));
            items.AddRange(FollowUpItems(row, context));
        }

        return items;
    }

    private static Task<List<JobCaptureRow>> JobCaptureRowsAsync(BlocwerkDbContext db, JobProgressReadContext context, CancellationToken ct)
    {
        var walls = context.Walls;
        var since = context.Since;
        return db.WallCaptures.AsNoTracking()
            .Where(c => walls == null || walls.Contains(c.WallId))
            .Where(c => c.Status != WallCaptureStatus.Draft)
            .Where(c => RunningStatuses.Contains(c.Status) || c.CompletedAt >= since || c.UpdatedAt >= since
                        || (c.TexturesJobId != null && c.TexturesJobId.StartsWith(CaptureTextureOutcome.RerenderMark))
                        || (c.SolveJobId != null && c.SolveJobId.StartsWith(CaptureResolveMark.Mark)))
            .OrderByDescending(c => c.CreatedAt)
            .Take(MaxRows)
            .Select(c => new JobCaptureRow(
                c.Id, c.WallId, c.Status, c.Progress, c.Stage, c.Error, c.CreatedAt, c.StartedAt, c.CompletedAt, c.UpdatedAt,
                c.Attempts, c.TimelineJson, c.FollowUpJson, c.TexturesJobId, c.SolveJobId))
            .ToListAsync(ct);
    }

    private static JobProgressItem? CaptureItem(JobCaptureRow row, List<CaptureTimelineEntry> timeline, JobProgressReadContext context)
    {
        var running = RunningStatuses.Contains(row.Status);
        if (!running && !(row.CompletedAt >= context.Since))
        {
            return null;
        }

        var stage = CaptureTimeline.StageOf(row.Status) ?? JsonNamingPolicy.CamelCase.ConvertName(row.Status.ToString());
        var stageStart = running ? CaptureTimeline.Open(timeline, stage)?.StartedAt : null;
        var (eta, source) = running ? context.FromHistory(JobKinds.Capture, stage, stageStart) : (null, null);
        var started = row.StartedAt ?? row.CreatedAt;
        return new JobProgressItem
        {
            Id = $"{JobKinds.Capture}:{row.Id}",
            Kind = JobKinds.Capture,
            State = CaptureState(row.Status),
            Stage = stage,
            Detail = row.Stage,
            Percent = JobEta.Percent(row.Progress),
            EtaSeconds = eta,
            EtaSource = source,
            StartedAt = started,
            UpdatedAt = row.UpdatedAt ?? started,
            EndedAt = running ? null : row.CompletedAt,
            LastError = row.Error,
            WallId = row.WallId,
            CaptureId = row.Id,
            Attempts = row.Attempts,
            Stages = timeline
                .Where(e => e.Stage is not (CaptureTimeline.Rerender or CaptureTimeline.Resolve))
                .Select(e => Span(e, context.Now))
                .ToList(),
        };
    }

    private static string CaptureState(WallCaptureStatus status) => status switch
    {
        WallCaptureStatus.Queued => JobStates.Queued,
        WallCaptureStatus.Failed => JobStates.Failed,
        _ when RunningStatuses.Contains(status) => JobStates.Running,
        _ => JobStates.Succeeded,
    };

    /// <summary>The newest texture re-render and re-solve of the capture, when one runs or ended within the window.</summary>
    private static IEnumerable<JobProgressItem> LaneItems(JobCaptureRow row, List<CaptureTimelineEntry> timeline, JobProgressReadContext context)
    {
        var lanes = new[]
        {
            (Lane: CaptureTimeline.Rerender, Kind: JobKinds.TextureRerender, Marked: CaptureTextureOutcome.IsRerendering(row.TexturesJobId)),
            (Lane: CaptureTimeline.Resolve, Kind: JobKinds.Resolve, Marked: CaptureResolveMark.IsResolving(row.SolveJobId)),
        };
        foreach (var (lane, kind, marked) in lanes)
        {
            var entry = timeline.LastOrDefault(e => e.Stage == lane);
            var running = marked || entry is { EndedAt: null };
            if (!running && !(entry?.EndedAt >= context.Since))
            {
                continue;
            }

            // A mark set before the timeline existed has no start: running, but since when is unknown.
            DateTimeOffset? start = running ? (entry is { EndedAt: null } ? entry.StartedAt : null) : entry!.StartedAt;
            yield return LaneItem(row, kind, lane, running ? null : entry, start, context);
        }
    }

    private static JobProgressItem LaneItem(
        JobCaptureRow row, string kind, string lane, CaptureTimelineEntry? ended, DateTimeOffset? start, JobProgressReadContext context)
    {
        var (eta, source) = ended is null ? context.FromHistory(kind, lane, start) : (null, null);
        var failed = ended?.Outcome == CaptureTimeline.Failed;
        return new JobProgressItem
        {
            Id = $"{kind}:{row.Id}",
            Kind = kind,
            State = ended is null ? JobStates.Running : failed ? JobStates.Failed : JobStates.Succeeded,
            Stage = lane,
            EtaSeconds = eta,
            EtaSource = source,
            StartedAt = start,
            UpdatedAt = ended?.EndedAt ?? row.UpdatedAt ?? start,
            EndedAt = ended?.EndedAt,
            LastError = failed ? CaptureFollowUpRecord.Parse(row.FollowUpJson).Note ?? row.Error : null,
            WallId = row.WallId,
            CaptureId = row.Id,
        };
    }

    private static JobStageSpan Span(CaptureTimelineEntry e, DateTimeOffset now) =>
        new(e.Stage, e.StartedAt, e.EndedAt, e.Outcome, Math.Round(((e.EndedAt ?? now) - e.StartedAt).TotalSeconds, 1));
}
