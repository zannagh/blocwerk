// <copyright file="PhotoHeaderQueryCounter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Data.Common;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>Counts the photo-header reads (<see cref="PanelPhotoInfoLoader.HeaderBytes"/> slices) a context runs.</summary>
public sealed class PhotoHeaderQueryCounter : DbCommandInterceptor
{
    private int count;

    /// <summary>Gets how many header queries ran.</summary>
    public int Count => count;

    /// <summary>A context on the harness database that reports to this counter.</summary>
    /// <param name="harness">The harness.</param>
    /// <returns>The context.</returns>
    public BlocwerkDbContext Context(WallTestHarness harness) => new SqliteBlocwerkDbContext(
        new DbContextOptionsBuilder<BlocwerkDbContext>().UseSqlite(harness.DbContextFactory.ConnectionString).AddInterceptors(this).Options);

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Observe(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Observe(command);
        return ValueTask.FromResult(result);
    }

    private void Observe(DbCommand command)
    {
        if (command.CommandText.Contains("substr(\"Photo\", 1,", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref count);
        }
    }
}
