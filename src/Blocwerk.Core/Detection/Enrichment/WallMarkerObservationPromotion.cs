using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>
/// Keeps a panel's marker observations in step with its photos when a staged photo goes live.
/// Observations are keyed by <c>FromStagedPhoto</c> and generation; without this the promoted photo's
/// markers would stay labelled "staged" forever and the replaced live photo's markers would linger.
/// </summary>
public static class WallMarkerObservationPromotion
{
    /// <summary>
    /// Drops the panel's observations of the photo being replaced and re-labels the staged photo's
    /// observations as live, under <paramref name="liveGeneration"/> — the generation the promoted
    /// holds carry. Changes are tracked only: they commit with the caller's own SaveChanges.
    /// </summary>
    public static async Task PromoteStagedAsync(BlocwerkDbContext db, Guid panelId, int liveGeneration)
    {
        var rows = await db.WallMarkerObservations
            .Where(o => o.WallPanelId == panelId)
            .ToListAsync();

        foreach (var row in rows)
        {
            if (row.FromStagedPhoto)
            {
                row.FromStagedPhoto = false;
                row.PanelGeneration = liveGeneration;
            }
            else
            {
                db.WallMarkerObservations.Remove(row);
            }
        }
    }
}
