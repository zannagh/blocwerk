// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>Logs one column's progress every <see cref="ContentHashBackfillOptions.ProgressInterval"/> and when it is done.</summary>
internal sealed class BackfillProgress
{
    private readonly ILogger logger;
    private readonly string column;
    private readonly TimeSpan interval;
    private readonly long started = Stopwatch.GetTimestamp();
    private long lastLogged = Stopwatch.GetTimestamp();

    public BackfillProgress(ILogger logger, string column, TimeSpan interval)
    {
        this.logger = logger;
        this.column = column;
        this.interval = interval;
    }

    public void Report(BackfillCount soFar)
    {
        if (Stopwatch.GetElapsedTime(lastLogged) < interval)
        {
            return;
        }

        lastLogged = Stopwatch.GetTimestamp();
        logger.LogInformation(
            "Content-hash backfill of {Column}: {Stamped} rows ({Megabytes:F0} MB) so far, {Seconds:F0} s",
            column, soFar.Stamped, soFar.Bytes / 1048576d, Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    public void Done(BackfillCount total)
    {
        if (total == BackfillCount.None)
        {
            return;
        }

        logger.LogInformation(
            "Content-hash backfill of {Column}: {Stamped} rows ({Megabytes:F0} MB) in {Seconds:F1} s; {Retry} left for a retry, {Missing} without their file",
            column, total.Stamped, total.Bytes / 1048576d, Stopwatch.GetElapsedTime(started).TotalSeconds, total.Retry, total.Missing);
    }
}
