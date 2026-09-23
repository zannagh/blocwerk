using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Read-only access to a capture's stored photos so an admin can reuse one as a panel photo. Nothing
/// here writes: the capture, its photos and its files stay exactly as they were.
/// </summary>
public sealed partial class WallCaptureService
{
    public async Task<IReadOnlyList<CapturePhotoResult>> GetPhotosAsync(Guid captureId)
    {
        var (db, _, _) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            var photos = await db.WallCapturePhotos.AsNoTracking()
                .Where(p => p.CaptureId == captureId)
                .OrderBy(p => p.Index)
                .ToListAsync();
            return photos.Select(p => ToResult(p, [])).ToList();
        }
    }

    public async Task<CapturePhotoFile> ReadPhotoAsync(Guid captureId, Guid photoId, CancellationToken ct)
    {
        var (db, _, capture) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            var photo = await db.WallCapturePhotos.AsNoTracking()
                            .FirstOrDefaultAsync(p => p.Id == photoId && p.CaptureId == captureId, ct)
                        ?? throw new InvalidOperationException("That photo is not part of this capture.");
            var bytes = await files.ReadAsync(photo.StoredPath, ct)
                        ?? throw new InvalidOperationException(
                            $"{photo.OriginalFileName ?? $"Photo {photo.Index}"} is no longer stored on the server.");
            return new CapturePhotoFile(capture.WallId, photo.Id, photo.OriginalFileName, bytes, photo.ContentType);
        }
    }
}
