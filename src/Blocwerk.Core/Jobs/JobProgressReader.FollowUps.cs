// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;

namespace Blocwerk.Core.Jobs;

/// <summary>Follow-up steps: the one running (the record's <c>running</c> mark) and those recorded within the window.</summary>
public sealed partial class JobProgressReader
{
    private static IEnumerable<JobProgressItem> FollowUpItems(JobCaptureRow row, JobProgressReadContext context)
    {
        if (row.FollowUpJson is null)
        {
            yield break;
        }

        var record = CaptureFollowUpRecord.Parse(row.FollowUpJson);
        if (record.Running is { } running)
        {
            var (eta, source) = context.FromHistory(JobKinds.FollowUp, running.Key, running.StartedAt);
            yield return FollowUpItem(row, running.Key) with
            {
                State = JobStates.Running,
                Detail = running.Title,
                EtaSeconds = eta,
                EtaSource = source,
                StartedAt = running.StartedAt,
                UpdatedAt = running.StartedAt,
            };
        }

        foreach (var step in record.Steps.Where(s => s.At >= context.Since && s.Key != record.Running?.Key))
        {
            yield return FollowUpItem(row, step.Key) with
            {
                State = step.Outcome switch
                {
                    CaptureFollowUpOutcome.Failed => JobStates.Failed,
                    CaptureFollowUpOutcome.Skipped => JobStates.Skipped,
                    _ => JobStates.Succeeded,
                },
                Detail = string.IsNullOrWhiteSpace(step.Summary) ? null : step.Summary,
                Percent = 100,
                StartedAt = step.StartedAt,
                UpdatedAt = step.At,
                EndedAt = step.At,
                LastError = step.Outcome == CaptureFollowUpOutcome.Failed ? step.Summary : null,
            };
        }
    }

    private static JobProgressItem FollowUpItem(JobCaptureRow row, string key) => new()
    {
        Id = $"{JobKinds.FollowUp}:{row.Id}:{key}",
        Kind = JobKinds.FollowUp,
        State = JobStates.Running,
        Stage = key,
        WallId = row.WallId,
        CaptureId = row.Id,
    };
}
