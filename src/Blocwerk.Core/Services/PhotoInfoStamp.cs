// <copyright file="PhotoInfoStamp.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which stored photo a panel (or legacy wall) row holds, read without its bytes: the row, its generation, the photo's
/// byte length and its last <see cref="TailBytes"/> bytes (entropy-coded pixel data right before a JPEG's end marker,
/// so another photo differs there). The revision is part of it too, so an in-place edit (a crop) can never collide with the old photo by sharing its length and tail. A promote, re-upload or crop changes at least one of them, so the stamp is the cache key
/// of whatever is derived from the photo alone (<see cref="Wall3DViewCache"/>) and nothing needs invalidating.
/// </summary>
/// <param name="RowId">The panel id, or the wall id for the legacy wall photo.</param>
/// <param name="Generation">The panel's generation (the wall's current generation for the legacy photo).</param>
/// <param name="Length">The photo's byte length.</param>
/// <param name="Tail">The photo's last bytes, hex.</param>
/// <param name="Revision">The panel's photo revision, bumped by an in-place edit such as a crop (0 for the legacy wall photo).</param>
internal readonly record struct PhotoInfoStamp(Guid RowId, int Generation, long Length, string Tail, int Revision)
{
    /// <summary>Bytes of the photo's end taken into the stamp.</summary>
    public const int TailBytes = 32;

    /// <summary>
    /// The stamps of the photos <paramref name="wanted"/> refers to, by row id; a row without a photo has none. Only the
    /// length (PostgreSQL takes it from the stored value's header) and the last bytes are read, never the whole photo.
    /// </summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="wanted">The photos (a null panel is the legacy wall photo).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The stamp per row id.</returns>
    public static async Task<Dictionary<Guid, PhotoInfoStamp>> LoadAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Wall3DPhotoKey> wanted, CancellationToken ct)
    {
        var panelIds = wanted.Where(k => k.PanelId.HasValue).Select(k => k.PanelId!.Value).Distinct().ToList();
        var rows = panelIds.Count == 0
            ? []
            : await db.Database
                .SqlQuery<PhotoStampRow>($"SELECT \"Id\", \"WallId\", \"Generation\", \"PhotoRevision\" AS \"Revision\", CAST(length(\"Photo\") AS bigint) AS \"Length\", substr(\"Photo\", length(\"Photo\") - {TailBytes - 1}, {TailBytes}) AS \"Tail\" FROM \"WallPanels\" WHERE \"Photo\" IS NOT NULL")
                .Where(r => r.WallId == wallId && panelIds.Contains(r.Id))
                .ToListAsync(ct);
        if (wanted.Any(k => k.PanelId is null))
        {
            rows.AddRange(await db.Database
                .SqlQuery<PhotoStampRow>($"SELECT \"Id\", \"Id\" AS \"WallId\", \"CurrentGeneration\" AS \"Generation\", 0 AS \"Revision\", CAST(length(\"Photo\") AS bigint) AS \"Length\", substr(\"Photo\", length(\"Photo\") - {TailBytes - 1}, {TailBytes}) AS \"Tail\" FROM \"Walls\" WHERE \"Photo\" IS NOT NULL")
                .Where(r => r.Id == wallId)
                .ToListAsync(ct));
        }

        return rows.ToDictionary(
            r => r.Id,
            r => new PhotoInfoStamp(r.Id, r.Generation, r.Length, Convert.ToHexString(r.Tail ?? []), r.Revision));
    }
}
