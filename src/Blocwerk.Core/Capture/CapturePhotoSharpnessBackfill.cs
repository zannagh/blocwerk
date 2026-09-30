// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Scores, once per start and off the startup path, every stored capture photo without a real sharpness
/// (<see cref="CapturePhotoSharpness"/>): the photos stored before the column existed, and the 0 every photo got before the
/// scorer read colour JPEGs. One photo at a time with a pause between, newest captures first, so a full decode never
/// takes more than one core for long. A photo whose file is gone (retention) or cannot be decoded stays unscored.
/// </summary>
public sealed class CapturePhotoSharpnessBackfill(
    RootDbContextFactory dbContextFactory,
    ICaptureFileStore files,
    WallCapturePipelineOptions options,
    ILogger<CapturePhotoSharpnessBackfill> logger) : BackgroundService
{
    /// <summary>Wait after start before the first photo, so the app's own startup work goes first.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Pause after each scored photo (keeps the CPU modest).</summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Scores the unscored photos. Returns how many got a real score.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            ids = await db.WallCapturePhotos.AsNoTracking()
                .Where(p => p.Sharpness == null || p.Sharpness <= 0)
                .OrderByDescending(p => p.UploadedAt)
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        var scored = 0;
        foreach (var id in ids)
        {
            if (await ScoreOneAsync(id, ct))
            {
                scored++;
                await Task.Delay(Pause, ct);
            }
        }

        return scored;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            var scored = await RunAsync(stoppingToken);
            if (scored > 0)
            {
                logger.LogInformation("Scored the sharpness of {Count} capture photo(s)", scored);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block anything: the pipeline scores what it needs itself, and the next start tries again.
            logger.LogWarning(ex, "Could not score the sharpness of the stored capture photos");
        }
    }

    private async Task<bool> ScoreOneAsync(Guid id, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var photo = await db.WallCapturePhotos.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (photo is null)
        {
            return false;
        }

        var scored = await CapturePhotoSharpness.ScoreIfMissingAsync(photo, files, options.PhotoSharpnessEdge, ct);
        if (!scored && !CapturePhotoSharpness.IsScored(photo.Sharpness))
        {
            photo.Sharpness = null; // the bogus 0 of a photo whose file is gone or undecodable: plainly unscored
        }

        await db.SaveChangesAsync(ct);
        return scored;
    }
}
