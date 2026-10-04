using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>The review pictures of the suggestions.</summary>
public sealed partial class HoldDuplicateService
{
    /// <inheritdoc/>
    public async Task<byte[]?> CropAsync(Guid wallId, Guid holdId, Guid otherId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var holds = await db.Holds.AsNoTracking()
                .Where(h => h.WallId == wallId && (h.Id == holdId || h.Id == otherId))
                .ToListAsync(ct);
            var focus = holds.FirstOrDefault(h => h.Id == holdId);
            var other = holds.FirstOrDefault(h => h.Id == otherId);
            if (focus is null || other is null || focus.WallPanelId != other.WallPanelId)
            {
                return null;
            }

            var photo = focus.WallPanelId is { } panelId
                ? await db.WallPanels.Where(p => p.Id == panelId && p.WallId == wallId).Select(p => p.Photo).FirstOrDefaultAsync(ct)
                : await db.Walls.Where(w => w.Id == wallId).Select(w => w.Photo).FirstOrDefaultAsync(ct);
            return photo is null ? null : await Task.Run(() => HoldPairCrop.Render(photo, focus, other), ct);
        }
    }
}
