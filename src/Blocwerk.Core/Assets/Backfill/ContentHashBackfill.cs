// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>
/// Fills the content hashes (design §6.2) of rows stored before PR 2.1 stamped them: every database blob column
/// (<see cref="ContentHashColumn.InDatabase"/>, hashed in SQL on Postgres), every stored-file column
/// (<see cref="ContentHashFileColumn.Stored"/>, streamed and written under an <c>xmin</c> guard) and the
/// level-of-detail ladder entries of the photo-real scenes.
/// </summary>
/// <remarks>
/// Everything goes through raw SQL and never through tracked entities: the context's hash stamp would put a hand-set hash
/// back, the photo-less projections hold null bytes beside real hashes, and raw SQL reaches neither the change journal
/// nor the feed (nothing about a row changes that a reader could see except its hash). It only ever fills NULL hashes,
/// so it is idempotent and resumes by itself: a restart (or a deploy cutting a pass off, which rolls back at most one
/// batch) simply starts over on what is still null. Not a deploy-busy hold for the same reason. Off the startup path,
/// throttled by <see cref="BackfillPacer"/>, and logged per column.
/// </remarks>
public sealed class ContentHashBackfill : BackgroundService
{
    private readonly RootDbContextFactory dbContextFactory;
    private readonly IWallImageStorage images;
    private readonly ICaptureFileStore captures;
    private readonly ContentHashBackfillOptions options;
    private readonly ILogger<ContentHashBackfill> logger;

    public ContentHashBackfill(
        RootDbContextFactory dbContextFactory,
        IWallImageStorage images,
        ICaptureFileStore captures,
        ContentHashBackfillOptions options,
        ILogger<ContentHashBackfill> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.images = images;
        this.captures = captures;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>One pass over every column: returns what it did in total.</summary>
    public async Task<BackfillCount> RunPassAsync(CancellationToken ct)
    {
        var blobs = new BlobHashBackfill(dbContextFactory.CreateDbContext, options, logger);
        var files = new FileHashBackfill(dbContextFactory.CreateDbContext, images, captures, options, logger);
        var total = BackfillCount.None;
        foreach (var column in ContentHashColumn.InDatabase)
        {
            total += await blobs.RunAsync(column, ct);
        }

        foreach (var column in ContentHashFileColumn.Stored)
        {
            total += await files.RunAsync(column, ct);
        }

        total += await new LadderHashBackfill(dbContextFactory.CreateDbContext, captures, options, logger).RunAsync(ct);
        return total;
    }

    /// <summary>
    /// Passes until nothing is left to retry (rows a writer held or changed meanwhile), at most
    /// <see cref="ContentHashBackfillOptions.MaxPasses"/>; returns the last pass's count.
    /// </summary>
    public async Task<BackfillCount> RunAsync(CancellationToken ct)
    {
        for (var pass = 1; ; pass++)
        {
            var count = await RunPassAsync(ct);
            if (count.Stamped > 0 || count.Retry > 0)
            {
                logger.LogInformation(
                    "Content-hash backfill pass {Pass}: {Stamped} rows ({Megabytes:F0} MB) hashed, {Retry} left for a retry, {Missing} without their file",
                    pass, count.Stamped, count.Bytes / 1048576d, count.Retry, count.Missing);
            }

            if (count.Retry == 0 || pass >= options.MaxPasses)
            {
                return count;
            }

            await Task.Delay(options.RetryDelay, ct);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.StartDelay, stoppingToken);
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the next start goes on with what is still null.
        }
        catch (Exception ex)
        {
            // Never take the app down: unhashed rows keep their legacy URLs, and the next start tries again.
            logger.LogWarning(ex, "Content-hash backfill failed; the next start tries again");
        }
    }
}
