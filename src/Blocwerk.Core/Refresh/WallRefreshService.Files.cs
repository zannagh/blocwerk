// <copyright file="WallRefreshService.Files.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The drop zone's files: photos go into the run's capture draft (stored, HEIC converted, EXIF and markers read,
/// as in the capture panel); videos are kept beside it until the 3D capture starts. Any admin of the wall may add
/// files to the run; they are stored as the run's starter, who owns its draft.
/// </summary>
public sealed partial class WallRefreshService
{
    /// <summary>At most this many videos per run (the capture takes one; several are joined).</summary>
    public const int MaxVideos = 6;

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> FileGates = new();

    public async Task EnsureCanUploadAsync(Guid refreshId, bool isVideo)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            EnsureUploading(refresh);
            if (isVideo)
            {
                EnsureVideosAllowed(refresh, null);
            }
        }
    }

    public async Task<CapturePhotoResult> AddPhotoAsync(Guid refreshId, string? fileName, byte[] bytes, CancellationToken ct)
    {
        WallRefresh refresh;
        var (db, current) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            EnsureUploading(current);
            refresh = current;
        }

        var captureId = refresh.CaptureId ?? throw new UserFacingException("This update takes no more photos.");

        // The capture numbers its photos as it stores them, so a drop's parallel uploads are stored one at a time.
        var gate = FileGates.GetOrAdd(refreshId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var scope = await actorFactory.CreateAsync(refresh.CreatedByUserId, ct);
            return await scope.Actors.Capture.AddPhotoAsync(captureId, fileName, bytes, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RefreshVideo> AddVideoAsync(Guid refreshId, string? fileName, Stream content, CancellationToken ct)
    {
        var name = fileName is { Length: > 256 } ? fileName[..256] : fileName;
        var extension = Path.GetExtension(name ?? string.Empty);
        if (!CaptureVideoFiles.Extensions.Contains(extension))
        {
            throw new UserFacingException($"{name ?? "The file"} is not an MP4 or MOV video.");
        }

        await EnsureCanUploadAsync(refreshId, isVideo: true);
        var stored = await files.SaveStreamAsync(content, extension, Options.MaxVideoBytes, ct);
        var video = new RefreshVideo(stored, name, new FileInfo(files.ResolvePhysicalPath(stored) ?? stored).Length);
        var gate = FileGates.GetOrAdd(refreshId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var (db, refresh) = await OpenRefreshAsync(refreshId);
            await using (db)
            {
                EnsureUploading(refresh);
                EnsureVideosAllowed(refresh, name);
                refresh.VideosJson = RefreshTimeline.Write(RefreshTimeline.Videos(refresh).Append(video).ToList());
                refresh.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                return video;
            }
        }
        catch
        {
            files.Delete(stored);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void EnsureUploading(WallRefresh refresh)
    {
        if (refresh.Status != WallRefreshStatus.Uploading)
        {
            throw new UserFacingException("The photos of this update are already being used; start a new update to add more.");
        }
    }

    private void EnsureVideosAllowed(WallRefresh refresh, string? name)
    {
        if (!captures.IsSplatConfigured)
        {
            throw new UserFacingException($"{name ?? "The video"} is skipped: videos are only used for the photo-real 3D view, which is not set up here.");
        }

        if (RefreshTimeline.Videos(refresh).Count >= MaxVideos)
        {
            throw new UserFacingException($"{name ?? "The video"} is skipped: an update takes at most {MaxVideos} videos.");
        }
    }
}
