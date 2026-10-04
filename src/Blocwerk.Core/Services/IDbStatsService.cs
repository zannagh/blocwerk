// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services;

/// <summary>One statement's timings from <c>pg_stat_statements</c>.</summary>
public sealed record DbStatementStat(string Query, long Calls, double TotalMs, double MeanMs, long Rows);

/// <summary>The read-only database timing report: top statements by total and by mean time.</summary>
public sealed record DbStatsReport(
    bool Available,
    string? Problem,
    DateTimeOffset? StatsReset,
    IReadOnlyList<DbStatementStat> ByTotalTime,
    IReadOnlyList<DbStatementStat> ByMeanTime);

/// <summary>Admin-only, read-only view of <c>pg_stat_statements</c> for this database.</summary>
public interface IDbStatsService
{
    Task<DbStatsReport> GetReportAsync(CancellationToken cancellationToken = default);
}
