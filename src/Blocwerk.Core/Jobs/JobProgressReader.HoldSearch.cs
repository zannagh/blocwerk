// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Jobs;

/// <summary>Hold searches: each wall's latest one while it runs and for the window after it ended.</summary>
public sealed partial class JobProgressReader
{
    private IEnumerable<JobProgressItem> HoldSearchItems(JobProgressReadContext context) =>
        (holdSearches?.All() ?? [])
            .Where(s => context.Scope.Covers(s.WallId) && (s.Active || s.EndedAt >= context.Since))
            .Select(s => HoldSearchItem(s, context));

    private static JobProgressItem HoldSearchItem(HoldSearchStatus s, JobProgressReadContext context)
    {
        var (eta, source) = s.State == JobStates.Running
            ? JobProgressReadContext.FromRate(s.Done, s.Total, 0, s.StartedAt, context.Now)
            : (null, null);
        return new JobProgressItem
        {
            Id = $"{JobKinds.HoldSearch}:{s.WallId}",
            Kind = JobKinds.HoldSearch,
            State = s.State,
            Stage = s.State == JobStates.Queued ? "queued" : "searching",
            Detail = s.State switch
            {
                JobStates.Running => string.Create(CultureInfo.InvariantCulture, $"{s.Done} of {s.Total} photos searched"),
                JobStates.Succeeded => s.Result is { } r ? $"{r.Proposals} possible new holds" : null,
                _ => null,
            },
            Percent = s.State == JobStates.Succeeded ? 100 : s.Total > 0 ? JobEta.Percent((double)s.Done / s.Total) : null,
            Step = s.Total > 0 ? s.Done : null,
            TotalSteps = s.Total > 0 ? s.Total : null,
            EtaSeconds = eta,
            EtaSource = source,
            StartedAt = s.StartedAt,
            UpdatedAt = s.UpdatedAt,
            EndedAt = s.EndedAt,
            LastError = s.Error,
            WallId = s.WallId,
        };
    }
}
