// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Jobs;

/// <summary>What every per-kind mapping of <see cref="JobProgressReader"/> needs: the scope, the window and the history.</summary>
/// <param name="Scope">The walls read.</param>
/// <param name="Since">Ended jobs are listed from here.</param>
/// <param name="Now">The time of the read.</param>
/// <param name="History">The stage durations of recent jobs.</param>
internal sealed record JobProgressReadContext(JobProgressScope Scope, DateTimeOffset Since, DateTimeOffset Now, JobStageHistory History)
{
    /// <summary>The scope's walls as a list for a query (null: every wall).</summary>
    public List<Guid>? Walls { get; } = Scope.WallIds?.ToList();

    /// <summary>Remaining time at the job's own rate, with its source; (null, null) when unknown.</summary>
    public static (double? Eta, string? Source) FromRate(double? done, double? total, double? anchor, DateTimeOffset? anchorAt, DateTimeOffset now)
    {
        var eta = JobEta.FromRate(done, total, anchor, anchorAt, now);
        return (eta, eta is null ? null : JobEtaSources.Rate);
    }

    /// <summary>Remaining time from the stage's history, with its source; (null, null) when unknown.</summary>
    public (double? Eta, string? Source) FromHistory(string kind, string stage, DateTimeOffset? startedAt)
    {
        var eta = JobEta.FromHistory(History.Median(kind, stage), startedAt is { } s ? Now - s : null);
        return (eta, eta is null ? null : JobEtaSources.History);
    }
}
