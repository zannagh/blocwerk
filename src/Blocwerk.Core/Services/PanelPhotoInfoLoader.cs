// <copyright file="PanelPhotoInfoLoader.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The <see cref="PanelPhotoInfo"/> (pixel size, EXIF focal length) of the hold photos a wall's placed holds
/// refer to, for <see cref="PanelCameraEstimator"/>'s planar pose. Only the first <see cref="HeaderBytes"/> of each
/// stored photo are read (<c>substr</c> in the database, the same in PostgreSQL and SQLite): a JPEG's EXIF block
/// and frame header precede its pixel data. A photo whose header does not fit there is read whole.
/// </summary>
public static class PanelPhotoInfoLoader
{
    /// <summary>Bytes read from the start of each photo.</summary>
    public const int HeaderBytes = 256 * 1024;

    /// <summary>The info of each photo in <paramref name="keys"/> that has a readable image.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall the photos belong to.</param>
    /// <param name="keys">The photos (a null panel is the legacy wall photo).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The info per photo.</returns>
    public static async Task<Dictionary<Wall3DPhotoKey, PanelPhotoInfo>> LoadAsync(
        BlocwerkDbContext db, Guid wallId, IEnumerable<Wall3DPhotoKey> keys, CancellationToken ct)
    {
        var wanted = keys.Distinct().ToList();
        var panelIds = wanted.Where(k => k.PanelId.HasValue).Select(k => k.PanelId!.Value).Distinct().ToList();
        var headers = panelIds.Count == 0
            ? []
            : await db.Database
                .SqlQuery<PhotoHeaderRow>($"SELECT \"Id\", \"WallId\", substr(\"Photo\", 1, {HeaderBytes}) AS \"Header\" FROM \"WallPanels\" WHERE \"Photo\" IS NOT NULL")
                .Where(r => r.WallId == wallId && panelIds.Contains(r.Id))
                .ToListAsync(ct);
        if (wanted.Any(k => k.PanelId is null))
        {
            headers.AddRange(await db.Database
                .SqlQuery<PhotoHeaderRow>($"SELECT \"Id\", \"Id\" AS \"WallId\", substr(\"Photo\", 1, {HeaderBytes}) AS \"Header\" FROM \"Walls\" WHERE \"Photo\" IS NOT NULL")
                .Where(r => r.Id == wallId)
                .ToListAsync(ct));
        }

        var result = new Dictionary<Wall3DPhotoKey, PanelPhotoInfo>();
        foreach (var key in wanted)
        {
            var id = key.PanelId ?? wallId;
            if (headers.FirstOrDefault(h => h.Id == id)?.Header is { } header
                && (PanelPhotoInfo.FromImage(header) ?? await WholeAsync(db, key, wallId, header, ct)) is { } info)
            {
                result[key] = info;
            }
        }

        return result;
    }

    /// <summary>The info from the whole photo, when its header was cut off; null when that does not help either.</summary>
    private static async Task<PanelPhotoInfo?> WholeAsync(BlocwerkDbContext db, Wall3DPhotoKey key, Guid wallId, byte[] header, CancellationToken ct)
    {
        if (header.Length < HeaderBytes)
        {
            return null;
        }

        var photo = key.PanelId is { } panelId
            ? await db.WallPanels.AsNoTracking().IgnoreQueryFilters()
                .Where(p => p.Id == panelId && p.WallId == wallId).Select(p => p.Photo).FirstOrDefaultAsync(ct)
            : await db.Walls.AsNoTracking().IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.Photo).FirstOrDefaultAsync(ct);
        return photo is null ? null : PanelPhotoInfo.FromImage(photo);
    }
}
