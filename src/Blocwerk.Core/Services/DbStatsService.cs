// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Blocwerk.Core.Services;

/// <summary>Reads <c>pg_stat_statements</c> (read-only, this database only, the 20 top statements per ordering).</summary>
public sealed class DbStatsService : IDbStatsService
{
    internal const int Top = 20;

    // Only statements that ran a few times count for the mean ordering, so one slow outlier does not top it.
    private const int MinCallsForMean = 3;

    private const string Columns = @"
SELECT left(regexp_replace(s.query, '\s+', ' ', 'g'), 400) AS ""Query"",
       s.calls AS ""Calls"",
       s.total_exec_time AS ""TotalMs"",
       s.mean_exec_time AS ""MeanMs"",
       s.rows AS ""Rows""
  FROM pg_stat_statements s
  JOIN pg_database d ON d.oid = s.dbid
 WHERE d.datname = current_database()";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;

    public DbStatsService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ICurrentUserService currentUserService)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
    }

    public async Task<DbStatsReport> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        if (user?.Role != IdentityRole.Admin)
        {
            throw new UnauthorizedAccessException("The database statistics are restricted to administrators.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            var installed = await db.Database
                .SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements') AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!installed)
            {
                return Unavailable("The pg_stat_statements extension is not created in this database (docker/prod/README.md, Diagnostics).");
            }

            var byTotal = await db.Database
                .SqlQueryRaw<DbStatementStat>(Columns + " ORDER BY s.total_exec_time DESC LIMIT " + Top)
                .ToListAsync(cancellationToken);
            var byMean = await db.Database
                .SqlQueryRaw<DbStatementStat>(Columns + " AND s.calls >= " + MinCallsForMean + " ORDER BY s.mean_exec_time DESC LIMIT " + Top)
                .ToListAsync(cancellationToken);
            var reset = await db.Database
                .SqlQueryRaw<DateTimeOffset?>("SELECT stats_reset AS \"Value\" FROM pg_stat_statements_info")
                .SingleOrDefaultAsync(cancellationToken);
            return new DbStatsReport(true, null, reset, byTotal, byMean);
        }
        catch (PostgresException ex)
        {
            // The extension exists but the library is not preloaded (55000).
            return Unavailable(ex.MessageText);
        }
    }

    private static DbStatsReport Unavailable(string problem) => new(false, problem, null, [], []);
}
