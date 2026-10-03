using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>Query shapes for reading walls without dragging their photo bytes along.</summary>
public static class WallQueryExtensions
{
    /// <summary>
    /// Selects every column of a wall EXCEPT the two image blobs (<see cref="Wall.Photo"/> and
    /// <see cref="Wall.StagedPhoto"/>, ~10 MB each on a real wall). Loading the whole entity and nulling the
    /// bytes afterwards still pulls them out of Postgres (de-TOAST + transfer) on every read.
    /// </summary>
    /// <remarks>
    /// The rows are NOT tracked (EF never tracks entities built in a projection); a caller that needs
    /// navigation fix-up attaches them. Every scalar added to <see cref="Wall"/> must be copied here;
    /// <c>WallProjectionTests</c> fails when one is missed.
    /// </remarks>
    /// <param name="walls">The wall query (query filters still apply).</param>
    /// <returns>Walls with <see cref="Wall.Photo"/> and <see cref="Wall.StagedPhoto"/> left null.</returns>
    public static IQueryable<Wall> WithoutPhotos(this IQueryable<Wall> walls) =>
        walls.Select(w => new Wall
        {
            Id = w.Id,
            Name = w.Name,
            Description = w.Description,
            PhotoContentType = w.PhotoContentType,
            StagedPhotoContentType = w.StagedPhotoContentType,
            StagedAt = w.StagedAt,
            StagedByUserId = w.StagedByUserId,
            StagingMode = w.StagingMode,
            OwnerId = w.OwnerId,
            ShareToken = w.ShareToken,
            Angle = w.Angle,
            BorderPoints = w.BorderPoints,
            IsActive = w.IsActive,
            CreatedAt = w.CreatedAt,
            LastResetAt = w.LastResetAt,
            CurrentGeneration = w.CurrentGeneration,
            UsesMultipleImages = w.UsesMultipleImages,
            UnderMaintenance = w.UnderMaintenance,
            MaintenanceByUserId = w.MaintenanceByUserId,
            AllowAnonymousKioskSetting = w.AllowAnonymousKioskSetting,
            AllowKioskKeyboardShortcuts = w.AllowKioskKeyboardShortcuts,
            LinksFinalizedGeneration = w.LinksFinalizedGeneration,
            GlyphsEnabled = w.GlyphsEnabled,
            MarkerSizeMm = w.MarkerSizeMm,
            VolumesHaveFlatSides = w.VolumesHaveFlatSides,
        });

    /// <summary>
    /// Loads every wall the context's viewer can see, minus the photo blobs, and attaches them. Run BEFORE
    /// a tracked query over rows that reference walls (<c>Activity.Wall</c>, <c>ClimbingSession.Wall</c>):
    /// EF then fixes the navigation up from the tracked rows, replacing <c>Include(x =&gt; x.Wall)</c>, which
    /// repeats the wall's photos for every joined row. A wall the viewer cannot see stays a null navigation,
    /// exactly as it did under the query filter.
    /// </summary>
    /// <param name="db">The viewer-stamped context.</param>
    /// <returns>A task that completes when the walls are attached.</returns>
    public static async Task AttachVisibleWallsWithoutPhotosAsync(this BlocwerkDbContext db)
    {
        var walls = await db.Walls.WithoutPhotos().ToListAsync();
        db.AttachRange(walls);
    }
}
