// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>Deletes a wall just before the second query a context runs: the wall vanishes in the middle of a build.</summary>
internal sealed class DeleteWallOnSecondReadInterceptor(Func<Task> deleteWall) : DbCommandInterceptor
{
    private int reads;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref reads) == 2)
        {
            await deleteWall();
        }

        return result;
    }
}
