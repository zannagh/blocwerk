using System.Text.Json;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>Stage 1: markers for every photo that has none recorded yet (uploads normally detect already).</summary>
public sealed partial class WallCaptureProcessor
{
    private async Task DetectMarkersAsync(CaptureRun run, CancellationToken ct)
    {
        var captureId = run.Capture.Id;
        var pending = (await LoadPhotosAsync(captureId, ct)).Where(p => p.MarkersJson is null).ToList();
        if (run.Capture.SolveJobId is not null || run.Capture.SfmJobId is not null || pending.Count == 0)
        {
            return;
        }

        for (var i = 0; i < pending.Count; i++)
        {
            await SetStageAsync(
                captureId, WallCaptureStatus.Detecting, 0.02 + (0.18 * i / pending.Count),
                $"Finding markers in photo {i + 1} of {pending.Count}", ct);
            var bytes = await files.ReadAsync(pending[i].StoredPath, ct)
                        ?? throw new CaptureFailedException($"Photo {pending[i].Index} is missing on the server. Please upload it again.");
            var markersJson = JsonSerializer.Serialize(await CaptureMarkerDetection.DetectAsync(markerDetection, bytes, run.Layout.DetectionOptions, ct));
            await using var db = dbContextFactory.CreateDbContext();
            await db.WallCapturePhotos
                .Where(p => p.Id == pending[i].Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.MarkersJson, markersJson), ct);
        }
    }
}
