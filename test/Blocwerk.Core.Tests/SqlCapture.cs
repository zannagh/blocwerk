using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>Records the SQL text of every command a context runs, so tests can assert what a page load selects.</summary>
public sealed class SqlCapture : DbCommandInterceptor
{
    // A blob column named in a select list, as opposed to an IS [NOT] NULL probe or a length() of it.
    private static readonly Regex BlobSelected = new(
        "(?<!length\\((?:\\w+\\.)?)\"(Photo|StagedPhoto|PreviousPhoto)\"(?! IS)(?!\\))",
        RegexOptions.Compiled);

    private readonly ConcurrentQueue<string> commands = new();

    public IReadOnlyList<string> Commands => [.. commands];

    public void Clear() => commands.Clear();

    /// <summary>Statements that fetch a photo column's BYTES from Walls or WallPanels (select list only).</summary>
    public IReadOnlyList<string> BlobReads() =>
        [.. Commands.Where(c =>
        {
            var from = c.IndexOf(" FROM ", StringComparison.Ordinal);
            return from > 0 && BlobSelected.IsMatch(c[..from]);
        })];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        commands.Enqueue(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }
}

/// <summary>A context factory over the harness database that routes every command through a <see cref="SqlCapture"/>.</summary>
public sealed class CapturingDbContextFactory : IDbContextFactory<BlocwerkDbContext>
{
    private readonly string connectionString;

    public CapturingDbContextFactory(string connectionString, SqlCapture capture)
    {
        this.connectionString = connectionString;
        Capture = capture;
    }

    public SqlCapture Capture { get; }

    public BlocwerkDbContext CreateDbContext() =>
        TestDb.Create(connectionString, Capture);
}
