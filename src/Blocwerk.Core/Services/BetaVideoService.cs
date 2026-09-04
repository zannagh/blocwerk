using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface IBetaVideoService
{
    /// <summary>
    /// Records a clip that has already been written to the beta-video store. The upload endpoint
    /// streams and (if needed) transcodes the file, then calls this to create the row.
    /// </summary>
    Task<BetaVideoInfo> AddVideoFromFileAsync(
        Guid boulderId,
        string storedName,
        long sizeBytes,
        string contentType,
        byte[]? thumbnail,
        string? fileName);

    /// <summary>
    /// Convenience path for in-memory bytes (tests, tiny clips): writes them to the store and
    /// records the row. <paramref name="thumbnail"/> is the poster frame, or null.
    /// </summary>
    Task<BetaVideoInfo> AddVideoAsync(
        Guid boulderId,
        byte[] data,
        string contentType,
        byte[]? thumbnail,
        string? fileName);

    /// <summary>Newest first. Metadata only — never the clip bytes.</summary>
    Task<List<BetaVideoInfo>> GetVideosAsync(Guid boulderId);

    /// <summary>The same list for an anonymous share-link viewer.</summary>
    Task<List<BetaVideoInfo>> GetVideosByShareTokenAsync(Guid boulderId, string shareToken);

    /// <summary>
    /// How to serve the clip (a disk path to stream, or legacy bytes). <paramref name="shareToken"/>
    /// takes the anonymous path when set; without it the caller must be signed in.
    /// </summary>
    Task<BetaVideoFile?> GetVideoFileAsync(Guid videoId, string? shareToken = null);

    /// <summary>The clip bytes (reads a disk-backed clip fully into memory — for small clips/tests only).</summary>
    Task<BetaVideoContent?> GetVideoContentAsync(Guid videoId, string? shareToken = null);

    /// <summary>The poster frame, or null when this clip has none.</summary>
    Task<BetaVideoContent?> GetThumbnailAsync(Guid videoId, string? shareToken = null);

    /// <summary>Deletes a clip (row and stored file). Only its uploader may.</summary>
    Task DeleteVideoAsync(Guid videoId);

    /// <summary>
    /// Throws unless the signed-in caller is allowed to add a clip to <paramref name="boulderId"/>.
    /// Called by the upload endpoint BEFORE a byte is written, so an unauthorized caller cannot make
    /// the server spend disk and a transcode on a file it will then refuse.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Nobody is signed in.</exception>
    /// <exception cref="InvalidOperationException">No such boulder, or the caller is not a member of its wall.</exception>
    Task EnsureCanUploadAsync(Guid boulderId);
}

public class BetaVideoService : IBetaVideoService
{
    /// <summary>The browser-produced poster frame is a small JPEG; anything larger is not one.</summary>
    private const int MaxThumbnailBytes = 512 * 1024;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IActivityLogService activityLogService;
    private readonly IBetaVideoStorage storage;
    private readonly ILogger<BetaVideoService> logger;
    private readonly IPushNotificationService? pushNotificationService;

    public BetaVideoService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService,
        IBetaVideoStorage storage,
        ILogger<BetaVideoService> logger,
        IPushNotificationService? pushNotificationService = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.activityLogService = activityLogService;
        this.storage = storage;
        this.logger = logger;
        this.pushNotificationService = pushNotificationService;
    }

    public async Task<BetaVideoInfo> AddVideoFromFileAsync(
        Guid boulderId,
        string storedName,
        long sizeBytes,
        string contentType,
        byte[]? thumbnail,
        string? fileName)
    {
        using var op = BlocwerkMetrics.TimeOperation("BetaVideo.Add");
        try
        {
            if (sizeBytes <= 0)
            {
                throw new InvalidOperationException("The video file is empty.");
            }

            if (string.IsNullOrWhiteSpace(contentType)
                || !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only video files can be uploaded as beta.");
            }

            if (thumbnail is { Length: > MaxThumbnailBytes })
            {
                // Not worth failing the upload over: drop the oversized poster frame and let the
                // tile fall back to its placeholder.
                thumbnail = null;
            }

            var user = await currentUserService.GetCurrentUserAsync();
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder is null)
            {
                logger.LogWarning("Cannot add beta video: boulder {BoulderId} not found for {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            // Boulder has no query filter of its own (only Wall does), so membership is checked
            // explicitly here rather than being assumed from the read having succeeded.
            await EnsureMemberAsync(db, boulder.WallId, user.Id);

            var video = new BetaVideo
            {
                BoulderId = boulderId,
                UploadedByUserId = user.Id,
                ContentType = contentType,
                FileName = Truncate(fileName, 256),
                SizeBytes = sizeBytes,
                StoragePath = storedName,
                Thumbnail = thumbnail,
            };

            db.BetaVideos.Add(video);
            await db.SaveChangesAsync();

            BlocwerkMetrics.RecordBetaVideoUploaded(boulder.WallId, sizeBytes);

            logger.LogInformation(
                "Beta video {VideoId} ({Bytes} bytes) added on boulder {BoulderId} by {UserId}",
                video.Id, sizeBytes, boulderId, user.Id);

            await activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.BetaVideoUploaded);

            // After the commit: tell the boulder's setters + creator. Guarded internally, so it can
            // never break or block the upload.
            if (pushNotificationService is not null)
            {
                await pushNotificationService.NotifyBetaAsync(boulderId, user.Id);
            }

            return new BetaVideoInfo(
                video.Id,
                boulderId,
                user.Id,
                user.Name,
                video.CreatedAt,
                video.ContentType,
                video.SizeBytes,
                thumbnail is { Length: > 0 });
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<BetaVideoInfo> AddVideoAsync(
        Guid boulderId,
        byte[] data,
        string contentType,
        byte[]? thumbnail,
        string? fileName)
    {
        if (data.Length == 0)
        {
            throw new InvalidOperationException("The video file is empty.");
        }

        var extension = Path.GetExtension(fileName ?? string.Empty);
        var temp = storage.CreateTempPath(extension);
        string storedName;
        try
        {
            await File.WriteAllBytesAsync(temp, data);
            storedName = storage.Commit(temp, extension);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }

        try
        {
            return await AddVideoFromFileAsync(boulderId, storedName, data.Length, contentType, thumbnail, fileName);
        }
        catch
        {
            storage.Delete(storedName);
            throw;
        }
    }

    public async Task<List<BetaVideoInfo>> GetVideosAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BetaVideo.List");
        try
        {
            // Same gate as the clip bytes: wall membership, decided by the wall query filter. The
            // list is metadata only, but it still names the people who uploaded.
            await using var db = await OpenReadableAsync(shareToken: null);

            return await ProjectAsync(db.BetaVideos
                .Where(v => v.BoulderId == boulderId
                            && db.Walls.Any(w => w.Id == v.Boulder.WallId)));
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<BetaVideoInfo>> GetVideosByShareTokenAsync(Guid boulderId, string shareToken)
    {
        using var op = BlocwerkMetrics.TimeOperation("BetaVideo.ListByShareToken");
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            return await ProjectAsync(db.BetaVideos
                .Where(v => v.BoulderId == boulderId
                            && v.Boulder.Wall.ShareToken == shareToken
                            && !v.Boulder.IsDraft));
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<BetaVideoFile?> GetVideoFileAsync(Guid videoId, string? shareToken = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("BetaVideo.GetContent");
        try
        {
            await using var db = await OpenReadableAsync(shareToken);
            var meta = await AccessibleVideos(db, videoId, shareToken)
                .Select(v => new { v.StoragePath, v.ContentType, v.FileName, HasData = v.Data != null })
                .FirstOrDefaultAsync();

            if (meta is null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(meta.StoragePath))
            {
                var path = storage.ResolvePhysicalPath(meta.StoragePath);
                return path is not null && File.Exists(path)
                    ? new BetaVideoFile(path, null, meta.ContentType, meta.FileName)
                    : null;
            }

            if (!meta.HasData)
            {
                return null;
            }

            var bytes = await AccessibleVideos(db, videoId, shareToken).Select(v => v.Data).FirstOrDefaultAsync();
            return bytes is { Length: > 0 }
                ? new BetaVideoFile(null, bytes, meta.ContentType, meta.FileName)
                : null;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<BetaVideoContent?> GetVideoContentAsync(Guid videoId, string? shareToken = null)
    {
        var file = await GetVideoFileAsync(videoId, shareToken);
        if (file is null)
        {
            return null;
        }

        var bytes = file.Bytes ?? await File.ReadAllBytesAsync(file.PhysicalPath!);
        return new BetaVideoContent(bytes, file.ContentType);
    }

    public Task<BetaVideoContent?> GetThumbnailAsync(Guid videoId, string? shareToken = null) =>
        LoadContentAsync(videoId, shareToken, thumbnail: true);

    public async Task DeleteVideoAsync(Guid videoId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BetaVideo.Delete");
        try
        {
            var user = await currentUserService.GetCurrentUserAsync();
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Grab just the stored file name (not the blob) before deleting the row, so the file
            // on disk can be removed too.
            var storedName = await db.BetaVideos
                .Where(v => v.Id == videoId && v.UploadedByUserId == user.Id)
                .Select(v => v.StoragePath)
                .FirstOrDefaultAsync();

            // ExecuteDelete rather than load-then-Remove: loading the entity would pull the whole
            // clip into memory just to throw it away.
            var deleted = await db.BetaVideos
                .Where(v => v.Id == videoId && v.UploadedByUserId == user.Id)
                .ExecuteDeleteAsync();

            if (deleted == 0)
            {
                logger.LogWarning("Cannot delete beta video {VideoId}: not found for {UserId}", videoId, user.Id);
                throw new InvalidOperationException("Beta video not found");
            }

            storage.Delete(storedName);
            logger.LogInformation("Beta video {VideoId} deleted by {UserId}", videoId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task EnsureCanUploadAsync(Guid boulderId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wallId = await db.Boulders
            .Where(b => b.Id == boulderId)
            .Select(b => (Guid?)b.WallId)
            .FirstOrDefaultAsync();

        if (wallId is null)
        {
            throw new InvalidOperationException("Boulder not found");
        }

        await EnsureMemberAsync(db, wallId.Value, user.Id);
    }

    private static Task<List<BetaVideoInfo>> ProjectAsync(IQueryable<BetaVideo> query) =>
        query
            .AsNoTracking()
            .OrderByDescending(v => v.CreatedAt)

            // The last argument is a boolean, not the bytes: poster frames are fetched by the
            // tiles themselves, one request each, so the list query stays blob-free.
            .Select(v => new BetaVideoInfo(
                v.Id,
                v.BoulderId,
                v.UploadedByUserId,

                // Server-side projection: the User.Name computed property is not mapped, so inline
                // its fallback (custom name when set, else the OAuth name) for EF to translate.
                v.UploadedBy.CustomDisplayName ?? v.UploadedBy.DisplayName,
                v.CreatedAt,
                v.ContentType,
                v.SizeBytes,
                v.Thumbnail != null && v.Thumbnail.Length > 0))
            .ToListAsync();

    /// <summary>
    /// Shared read path for both blobs. Access mirrors the wall photo endpoints: a share token
    /// must match the boulder's wall, and without one the caller has to be signed in.
    /// </summary>
    private async Task<BetaVideoContent?> LoadContentAsync(Guid videoId, string? shareToken, bool thumbnail)
    {
        using var op = BlocwerkMetrics.TimeOperation(thumbnail ? "BetaVideo.GetThumbnail" : "BetaVideo.GetContent");
        try
        {
            await using var db = await OpenReadableAsync(shareToken);
            var bytes = await AccessibleVideos(db, videoId, shareToken).Select(v => v.Thumbnail).FirstOrDefaultAsync();
            return bytes is { Length: > 0 }
                ? new BetaVideoContent(bytes, "image/jpeg")
                : null;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Access mirrors the wall photo endpoints: a share token must match the boulder's wall, and
    /// without one the caller has to be a MEMBER of the boulder's wall — being signed in at all is
    /// not enough. The context therefore carries the caller's id on the non-share path, so the wall
    /// query filter becomes the membership check that <see cref="AccessibleVideos"/> leans on; the
    /// share path leaves it open (Guid.Empty) because an anonymous viewer has no membership.
    /// </summary>
    private async Task<BlocwerkDbContext> OpenReadableAsync(string? shareToken)
    {
        var currentUserId = Guid.Empty;
        if (string.IsNullOrEmpty(shareToken))
        {
            var user = await currentUserService.GetCurrentUserAsync();
            currentUserId = user.Id;
        }

        var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = currentUserId;
        return db;
    }

    /// <summary>
    /// The clip, if this caller may have it. <see cref="BetaVideo"/> carries no query filter of its
    /// own, so the non-share branch reaches the only filtered entity in the model — the wall — and
    /// lets its filter decide. Without that predicate the id alone was the whole check and any
    /// signed-in session could read every clip in the installation.
    /// </summary>
    private static IQueryable<BetaVideo> AccessibleVideos(BlocwerkDbContext db, Guid videoId, string? shareToken)
    {
        var query = db.BetaVideos.AsNoTracking().Where(v => v.Id == videoId);
        return string.IsNullOrEmpty(shareToken)
            ? query.Where(v => db.Walls.Any(w => w.Id == v.Boulder.WallId))
            : query.Where(v => v.Boulder.Wall.ShareToken == shareToken && !v.Boulder.IsDraft);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value
        : value[..max];

    /// <summary>
    /// Beta clips are for wall members. The boulder table carries no query filter, so this is the
    /// only thing standing between a signed-in stranger and every boulder in the installation.
    /// </summary>
    private static async Task EnsureMemberAsync(BlocwerkDbContext db, Guid wallId, Guid userId)
    {
        var isMember = await db.WallMembers.AnyAsync(m => m.WallId == wallId && m.UserId == userId);
        if (!isMember)
        {
            throw new InvalidOperationException("Only wall members can do this");
        }
    }
}
