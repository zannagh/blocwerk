using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>The outcome of normalizing one clip, for the worker's log.</summary>
public enum BetaVideoNormalizeOutcome
{
    Ready,
    Failed,
    Skipped,
}

/// <summary>
/// Normalizes a single beta clip to a web-safe rendition and replaces the served file atomically.
/// This is the ONE path both new uploads and the admin backfill funnel through: they only set a
/// clip to <see cref="BetaVideoEncodingStatus.Pending"/>; the worker calls this to do the work.
/// </summary>
/// <remarks>
/// A singleton (its dependencies all are), so it takes <see cref="RootDbContextFactory"/> — a hosted
/// worker has no session — and opens a short-lived context per step. Every failure is contained and
/// recorded as <see cref="BetaVideoEncodingStatus.Failed"/>; a bad clip never throws out of here.
/// </remarks>
public sealed class BetaVideoNormalizer
{
    private const int MaxErrorLength = 1024;

    private readonly RootDbContextFactory dbContextFactory;
    private readonly IBetaVideoStorage storage;
    private readonly IVideoTranscoder transcoder;
    private readonly BlocwerkSettings settings;
    private readonly ILogger<BetaVideoNormalizer> logger;

    public BetaVideoNormalizer(
        RootDbContextFactory dbContextFactory,
        IBetaVideoStorage storage,
        IVideoTranscoder transcoder,
        BlocwerkSettings settings,
        ILogger<BetaVideoNormalizer> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.storage = storage;
        this.transcoder = transcoder;
        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>Processes one clip end to end. Returns the outcome; never throws for a bad clip.</summary>
    public async Task<BetaVideoNormalizeOutcome> ProcessAsync(Guid videoId, CancellationToken cancellationToken)
    {
        string? legacyTemp = null;
        string? outPath = null;
        try
        {
            // The metadata read and the Processing flip are INSIDE the try so a transient DB error on
            // either degrades to a logged failure (below) rather than throwing out of here — the whole
            // point of this method is that a bad clip or DB blip never propagates to the worker loop.
            var meta = await LoadMetaAsync(videoId, cancellationToken);
            if (meta is null)
            {
                return BetaVideoNormalizeOutcome.Skipped;
            }

            await SetStatusAsync(videoId, BetaVideoEncodingStatus.Processing, cancellationToken);

            var source = await ResolveSourceAsync(videoId, meta, cancellationToken);
            legacyTemp = source.LegacyTemp;

            var probe = await transcoder.ProbeAsync(source.Path, cancellationToken);
            outPath = storage.CreateTempPath(".mp4");

            // Only take the cheap remux for a clip that is BOTH web-safe AND already small enough (≤720p,
            // near the target bitrate) to serve as the flaky-network MP4 fallback; an already-H.264 4K clip
            // is otherwise routed to the size-capping transcode. GetSourceBytes never throws (see below), so
            // an unreadable size falls through to CanRemux's safe default (transcode).
            var sourceBytes = GetSourceBytes(source.Path);
            var result = FfmpegVideoTranscoder.CanRemux(probe, sourceBytes, settings.BetaVideo.TargetVideoBitsPerSecond)
                ? await transcoder.RemuxAsync(source.Path, outPath, cancellationToken)
                : await transcoder.TranscodeAsync(source.Path, outPath, cancellationToken);

            // The HLS ladder is additive on top of the progressive MP4 fallback: build it best-effort,
            // and if it fails the clip is still Ready on the MP4 (HasHls stays false — no regression).
            var hasHls = await TryBuildHlsAsync(videoId, source.Path, probe, cancellationToken);

            // Poster frame from the NORMALIZED MP4 (already H.264 + auto-rotated upright), never the
            // possibly-HEVC/rotated original. Best-effort and non-throwing: a null poster leaves any
            // existing thumbnail untouched and never fails normalization. Grab it while outPath still
            // exists — Commit moves the file into the store below.
            var poster = await transcoder.ExtractPosterAsync(outPath, probe.DurationSeconds, cancellationToken);

            var storedName = storage.Commit(outPath, ".mp4");
            outPath = null; // ownership moved into the store

            await MarkReadyAsync(videoId, storedName, result, hasHls, poster, cancellationToken);

            // The row now points at the fresh file, so the previous disk rendition (if any) is safe to
            // drop. Legacy clips had their bytes in the row, cleared by MarkReadyAsync.
            if (!string.IsNullOrEmpty(meta.StoragePath) && meta.StoragePath != storedName)
            {
                storage.Delete(meta.StoragePath);
            }

            logger.LogInformation("Beta clip {VideoId} normalized ({Bytes} bytes)", videoId, result.SizeBytes);
            return BetaVideoNormalizeOutcome.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: leave the clip Processing so the next start re-picks it (the worker resets
            // stale Processing rows on boot). Do not record a failure.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Beta clip {VideoId} could not be normalized; leaving the original in place.", videoId);
            await MarkFailedAsync(videoId, ex.Message, cancellationToken);
            return BetaVideoNormalizeOutcome.Failed;
        }
        finally
        {
            // Both are full temp paths under the store's tmp/ folder (not stored names), so delete
            // them directly rather than through the store's name-based Delete.
            DeleteIfExists(legacyTemp);
            DeleteIfExists(outPath);
        }
    }

    /// <summary>
    /// Builds the HLS ladder into a fresh build directory and atomically commits it, returning whether
    /// it succeeded. A shutdown cancel propagates (the clip stays Processing and is re-picked); any other
    /// failure — a killed/timed-out ffmpeg included — is swallowed to false, with the build and any prior
    /// ladder cleaned up so a half-written ladder is never left behind or served.
    /// </summary>
    private async Task<bool> TryBuildHlsAsync(Guid videoId, string sourcePath, VideoProbeResult probe, CancellationToken ct)
    {
        try
        {
            var buildDir = storage.CreateHlsBuildDirectory(videoId);
            await transcoder.TranscodeHlsAsync(sourcePath, buildDir, probe, ct);
            storage.CommitHlsDirectory(videoId);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HLS ladder for beta clip {VideoId} failed; serving the MP4 fallback only.", videoId);
            TryDeleteHls(videoId);
            return false;
        }
    }

    private void TryDeleteHls(Guid videoId)
    {
        try
        {
            storage.DeleteHlsDirectory(videoId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clean up the HLS output for beta clip {VideoId}.", videoId);
        }
    }

    private async Task<BetaVideoMeta?> LoadMetaAsync(Guid videoId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.BetaVideos
            .AsNoTracking()
            .Where(v => v.Id == videoId)
            .Select(v => new BetaVideoMeta(v.StoragePath, v.FileName, v.Data != null))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Resolves the bytes to encode: the stored file, or a temp file written from legacy bytea.</summary>
    private async Task<BetaVideoSource> ResolveSourceAsync(Guid videoId, BetaVideoMeta meta, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(meta.StoragePath))
        {
            var path = storage.ResolvePhysicalPath(meta.StoragePath);
            if (path is null || !File.Exists(path))
            {
                throw new InvalidOperationException("The stored clip file is missing.");
            }

            return new BetaVideoSource(path, null);
        }

        if (!meta.HasData)
        {
            throw new InvalidOperationException("The clip has neither a stored file nor legacy bytes.");
        }

        await using var db = dbContextFactory.CreateDbContext();
        var bytes = await db.BetaVideos.AsNoTracking().Where(v => v.Id == videoId).Select(v => v.Data).FirstOrDefaultAsync(ct);
        if (bytes is not { Length: > 0 })
        {
            throw new InvalidOperationException("The legacy clip bytes could not be read.");
        }

        var extension = Path.GetExtension(meta.FileName ?? string.Empty);
        var temp = storage.CreateTempPath(string.IsNullOrEmpty(extension) ? ".bin" : extension);
        await File.WriteAllBytesAsync(temp, bytes, ct);
        return new BetaVideoSource(temp, temp);
    }

    private async Task SetStatusAsync(Guid videoId, BetaVideoEncodingStatus status, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        await db.BetaVideos
            .Where(v => v.Id == videoId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.EncodingStatus, status), ct);
    }

    private async Task MarkReadyAsync(
        Guid videoId, string storedName, VideoTranscodeResult result, bool hasHls, byte[]? poster, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        await db.BetaVideos
            .Where(v => v.Id == videoId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.StoragePath, storedName)
                .SetProperty(v => v.ContentType, result.ContentType)
                .SetProperty(v => v.SizeBytes, result.SizeBytes)
                .SetProperty(v => v.Data, (byte[]?)null)
                .SetProperty(v => v.HasHls, hasHls)
                .SetProperty(v => v.EncodingStatus, BetaVideoEncodingStatus.Ready)
                .SetProperty(v => v.LastEncodedUtc, DateTimeOffset.UtcNow)
                .SetProperty(v => v.EncodingError, (string?)null), ct);

        // Server-side poster is the source of truth: overwrite on every re-encode when we produced one.
        // A null poster (generation failed) must NOT wipe a thumbnail the clip already had, so it is a
        // separate, conditional write rather than part of the update above.
        if (poster is { Length: > 0 })
        {
            await db.BetaVideos
                .Where(v => v.Id == videoId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Thumbnail, poster), ct);
        }
    }

    private async Task MarkFailedAsync(Guid videoId, string error, CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            await db.BetaVideos
                .Where(v => v.Id == videoId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.EncodingStatus, BetaVideoEncodingStatus.Failed)
                    .SetProperty(v => v.EncodingError, Truncate(error)), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the normalize failure for beta clip {VideoId}.", videoId);
        }
    }

    /// <summary>
    /// The source file's length in bytes for the remux-vs-transcode bitrate estimate — the on-disk clip,
    /// or the temp file written from legacy bytea. Returns 0 (never throws) when the size cannot be read,
    /// which <see cref="FfmpegVideoTranscoder.CanRemux"/> treats as out of bounds → transcode.
    /// </summary>
    private long GetSourceBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the source size for a beta clip; transcoding to be safe.");
            return 0;
        }
    }

    private void DeleteIfExists(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // The finally must not throw: an IOException from a locked/vanished temp file cannot be
        // allowed to mask the real outcome or escape the method.
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete beta normalize temp file {Path}.", path);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];

    private sealed record BetaVideoMeta(string? StoragePath, string? FileName, bool HasData);

    private sealed record BetaVideoSource(string Path, string? LegacyTemp);
}
