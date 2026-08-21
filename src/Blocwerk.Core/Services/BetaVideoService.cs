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
    /// Stores a clip for a boulder. <paramref name="thumbnail"/> is the poster frame the browser
    /// grabbed; pass null when it could not produce one.
    /// </summary>
    Task<BetaVideoInfo> AddVideoAsync(
        Guid boulderId,
        byte[] data,
        string contentType,
        byte[]? thumbnail,
        string? fileName);

    /// <summary>Newest first. Metadata only — the clips stay in the database.</summary>
    Task<List<BetaVideoInfo>> GetVideosAsync(Guid boulderId);

    /// <summary>The same list for an anonymous share-link viewer.</summary>
    Task<List<BetaVideoInfo>> GetVideosByShareTokenAsync(Guid boulderId, string shareToken);

    /// <summary>
    /// The clip bytes. <paramref name="shareToken"/> takes the anonymous path when set; without it
    /// the caller must be signed in.
    /// </summary>
    Task<BetaVideoContent?> GetVideoContentAsync(Guid videoId, string? shareToken = null);

    /// <summary>The poster frame, or null when this clip has none.</summary>
    Task<BetaVideoContent?> GetThumbnailAsync(Guid videoId, string? shareToken = null);

    /// <summary>Deletes a clip. Only its uploader may.</summary>
    Task DeleteVideoAsync(Guid videoId);
}

public class BetaVideoService : IBetaVideoService
{
    /// <summary>
    /// Upload cap. The clip travels over the SignalR circuit, whose MaximumReceiveMessageSize is
    /// 64 MB (see Program.cs) — staying well under that leaves room for the poster frame and the
    /// message framing, and keeps a single bytea row a sane size.
    /// </summary>
    public const long MaxVideoBytes = 48L * 1024 * 1024;

    /// <summary>The browser-produced poster frame is a small JPEG; anything larger is not one.</summary>
    private const int MaxThumbnailBytes = 512 * 1024;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IActivityLogService activityLogService;
    private readonly ILogger<BetaVideoService> logger;

    public BetaVideoService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService,
        ILogger<BetaVideoService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.activityLogService = activityLogService;
        this.logger = logger;
    }

    public async Task<BetaVideoInfo> AddVideoAsync(
        Guid boulderId,
        byte[] data,
        string contentType,
        byte[]? thumbnail,
        string? fileName)
    {
        using var op = BlocwerkMetrics.TimeOperation("BetaVideo.Add");
        try
        {
            if (data.Length == 0)
            {
                throw new InvalidOperationException("The video file is empty.");
            }

            if (data.Length > MaxVideoBytes)
            {
                throw new InvalidOperationException($"Video too large (max {MaxVideoBytes / (1024 * 1024)} MB).");
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

            var video = new BetaVideo
            {
                BoulderId = boulderId,
                UploadedByUserId = user.Id,
                ContentType = contentType,
                FileName = Truncate(fileName, 256),
                SizeBytes = data.Length,
                Data = data,
                Thumbnail = thumbnail,
            };

            db.BetaVideos.Add(video);
            await db.SaveChangesAsync();

            BlocwerkMetrics.RecordBetaVideoUploaded(boulder.WallId, data.Length);

            logger.LogInformation(
                "Beta video {VideoId} ({Bytes} bytes) added on boulder {BoulderId} by {UserId}",
                video.Id, data.Length, boulderId, user.Id);

            await activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.BetaVideoUploaded);

            return new BetaVideoInfo(
                video.Id,
                boulderId,
                user.Id,
                user.DisplayName,
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

    public async Task<List<BetaVideoInfo>> GetVideosAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("BetaVideo.List");
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            return await ProjectAsync(db.BetaVideos.Where(v => v.BoulderId == boulderId));
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

    public Task<BetaVideoContent?> GetVideoContentAsync(Guid videoId, string? shareToken = null) =>
        LoadContentAsync(videoId, shareToken, thumbnail: false);

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

            logger.LogInformation("Beta video {VideoId} deleted by {UserId}", videoId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
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
                v.UploadedBy.DisplayName,
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
            if (string.IsNullOrEmpty(shareToken))
            {
                await currentUserService.GetCurrentUserAsync();
            }

            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            var query = db.BetaVideos.AsNoTracking().Where(v => v.Id == videoId);
            if (!string.IsNullOrEmpty(shareToken))
            {
                query = query.Where(v => v.Boulder.Wall.ShareToken == shareToken && !v.Boulder.IsDraft);
            }

            var row = thumbnail
                ? await query.Select(v => new { Data = v.Thumbnail, ContentType = "image/jpeg" }).FirstOrDefaultAsync()
                : await query.Select(v => new { Data = (byte[]?)v.Data, v.ContentType }).FirstOrDefaultAsync();

            return row?.Data is { Length: > 0 } bytes
                ? new BetaVideoContent(bytes, row.ContentType)
                : null;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value
        : value[..max];
}
