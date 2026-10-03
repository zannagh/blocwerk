// <copyright file="WallRefreshProcessor.Videos.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The visit's videos: the capture takes one, so several are joined losslessly when their formats match, else the
/// longest is used (<see cref="ICaptureVideoJoiner"/>). The note says which, on the 3D step.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    private async Task<string?> AttachVideosAsync(WallRefresh refresh, WallRefreshActors actors, Guid captureId, CancellationToken ct)
    {
        var videos = RefreshTimeline.Videos(refresh);
        if (videos.Count == 0)
        {
            return null;
        }

        if (!actors.Capture.IsSplatConfigured)
        {
            return "The videos are not used: the photo-real view is not set up here.";
        }

        var paths = videos.Select(v => files.ResolvePhysicalPath(v.StoredName)).OfType<string>().Where(File.Exists).ToList();
        if (paths.Count == 0)
        {
            return "The videos are gone from the server, so none is used.";
        }

        VideoJoinResult joined;
        try
        {
            joined = videoJoiner is null
                ? new VideoJoinResult(paths[0], false, "Only the first video is used.")
                : await videoJoiner.JoinAsync(paths, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            logger.LogWarning(ex, "Wall refresh {RefreshId}: joining the videos failed", refresh.Id);
            joined = new VideoJoinResult(paths[0], false, $"Joining the videos failed ({ex.Message}); only the first one is used.");
        }

        try
        {
            await using (var stream = File.OpenRead(joined.Path))
            {
                var name = joined.Joined ? $"joined{Path.GetExtension(joined.Path)}" : Path.GetFileName(joined.Path);
                await actors.Capture.AddVideoAsync(captureId, NameFor(videos, joined, name), stream, ct);
            }

            // The capture now holds its own copy; the dropped originals are no longer needed.
            foreach (var video in videos)
            {
                files.Delete(video.StoredName);
            }

            return videos.Count == 1 ? "With the walk-along video." : joined.Note;
        }
        catch (Exception ex) when (ex is UserFacingException or IOException)
        {
            return $"The video was not used: {ex.Message}";
        }
        finally
        {
            if (joined.Joined)
            {
                File.Delete(joined.Path);
            }
        }
    }

    private string NameFor(IReadOnlyList<RefreshVideo> videos, VideoJoinResult joined, string fallback)
    {
        if (joined.Joined)
        {
            return fallback;
        }

        var match = videos.FirstOrDefault(v => files.ResolvePhysicalPath(v.StoredName) == joined.Path);
        return match?.FileName ?? fallback;
    }
}
