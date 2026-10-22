// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>
/// <c>wall.manifest</c> (design §6.4): one coarse row per wall and save whenever a manifest input changes, alongside the
/// entity's own row, with no sha attached. Inputs are the committed photos (panel, wall, gallery), the wall's
/// generation, and the active 3D model with its texture and splat files. Staged photos are not inputs.
/// </summary>
/// <remarks>
/// <para>
/// A texture or splat row knows its wall only through its model, and counts only for the active one: the tracked model
/// (skipped when inactive, e.g. a preview install), else the save's
/// <see cref="FeedScope"/>. One with neither writes nothing; the server's cached manifest ages out instead
/// (<c>WallManifestService.MaxAge</c>). Reset photos need no case: a reset is written only in the promote that moves
/// <see cref="Wall.CurrentGeneration"/>.
/// </para>
/// <para>
/// Bulk writes (the privacy rewrite, the backfill) bypass mappers; PR 1.3 and <c>ManifestBackfillAnnouncer</c> write
/// their rows. Video thumbnails write no <c>wall.manifest</c>: they are cached on use, and <c>video.*</c> rows already
/// make the server rebuild its copy.
/// </para>
/// </remarks>
internal sealed class ManifestFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } =
        [typeof(WallPanel), typeof(Wall), typeof(WallImage), typeof(WallGeometryModel), typeof(WallGeometryTexture), typeof(WallGeometrySplat)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        if (WallOf(entry) is not { } wallId || context.Rows.Any(r => r.Kind == FeedKinds.WallManifest && r.WallId == wallId))
        {
            return;
        }

        context.Add(StructureRows.Wall(FeedKinds.WallManifest, wallId, wallId, null));
    }

    private static Guid? WallOf(EntityEntry entry) => entry.Entity switch
    {
        WallPanel panel when Touches(entry, nameof(WallPanel.Photo), nameof(WallPanel.PhotoSha256)) => panel.WallId,
        Wall wall when entry.State == EntityState.Modified
            && AnyModified(entry, nameof(Wall.Photo), nameof(Wall.PhotoSha256), nameof(Wall.CurrentGeneration)) => wall.Id,
        WallImage image when Touches(entry, nameof(WallImage.Sha256), nameof(WallImage.StoragePath)) => image.WallId,
        WallGeometryModel model when ActiveChanged(entry, model) => model.WallId,
        WallGeometryTexture when Touches(entry, nameof(WallGeometryTexture.StoredPath), nameof(WallGeometryTexture.Sha256),
            nameof(WallGeometryTexture.MaskStoredPath), nameof(WallGeometryTexture.MaskSha256)) => ModelWall(entry),
        WallGeometrySplat when Touches(entry, nameof(WallGeometrySplat.StoredPath), nameof(WallGeometrySplat.Sha256),
            nameof(WallGeometrySplat.MobileStoredPath), nameof(WallGeometrySplat.MobileSha256), nameof(WallGeometrySplat.LodLevelsJson)) => ModelWall(entry),
        _ => null,
    };

    // An added or deleted row is an input when it holds any of the columns; a modified one when it changed any of them.
    private static bool Touches(EntityEntry entry, params string[] properties) => entry.State switch
    {
        EntityState.Added or EntityState.Deleted => properties.Any(p => FeedEntryValues.Value<object?>(entry, p) is not null),
        EntityState.Modified => AnyModified(entry, properties),
        _ => false,
    };

    private static bool AnyModified(EntityEntry entry, params string[] properties) =>
        properties.Any(p => entry.Property(p).IsModified);

    private static bool ActiveChanged(EntityEntry entry, WallGeometryModel model)
    {
        var wasActive = entry.State != EntityState.Added && entry.OriginalValues.GetValue<bool>(nameof(WallGeometryModel.IsActive));
        var isActive = entry.State != EntityState.Deleted && model.IsActive;
        return wasActive != isActive;
    }

    // Only the active model's files are manifest inputs: a preview install or an inactive model writes nothing. An untracked
    // model is unknown, so the save's scope (if any) decides.
    private static Guid? ModelWall(EntityEntry entry) =>
        entry.Reference(nameof(WallGeometryTexture.GeometryModel)).CurrentValue is WallGeometryModel model
            ? (model.IsActive ? model.WallId : null)
            : FeedScope.Active?.WallId;
}
