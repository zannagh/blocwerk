using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Deletes full-resolution masters that no wall row points at any more. Always run AFTER the
/// column change has been saved: the check is "does any live or staged column still name this
/// file", so a promotion that merely moved a path from the staged to the live column keeps its
/// file, while a genuinely retired or discarded one is removed.
/// </summary>
internal static class WallPhotoMasterCleanup
{
    public static async Task DeleteUnreferencedAsync(
        BlocwerkDbContext db,
        IWallPhotoMasterStorage? storage,
        IEnumerable<string?> candidates,
        CancellationToken ct = default)
    {
        if (storage is null)
        {
            return;
        }

        foreach (var path in candidates.Where(p => !string.IsNullOrEmpty(p)).Distinct())
        {
            var stillReferenced = await db.Walls
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(
                    w => w.OrthoMasterPath == path
                        || w.AngledMasterPath == path
                        || w.StagedOrthoMasterPath == path
                        || w.StagedAngledMasterPath == path,
                    ct);

            if (!stillReferenced)
            {
                storage.Delete(path);
            }
        }
    }
}
