using System.Linq.Expressions;
using System.Text.Json;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>One stored photo: how to read it, and how to replace it only if it still holds the bytes that were read.</summary>
/// <param name="Load">Reads the photo, or null when the slot is empty.</param>
/// <param name="Save">Writes (db, original, clean); false when the stored bytes changed since they were read.</param>
/// <param name="Repair">
/// (db, apply): whether the row's metadata has drifted from the stored bytes, putting it right when
/// <c>apply</c> is set; null for a slot whose bytes and metadata are one column and cannot drift.
/// </param>
internal sealed record WallPhotoSlot(
    Func<BlocwerkDbContext, CancellationToken, Task<byte[]?>> Load,
    Func<BlocwerkDbContext, byte[], byte[], CancellationToken, Task<bool>> Save,
    Func<BlocwerkDbContext, bool, CancellationToken, Task<bool>>? Repair = null);

/// <summary>Where a wall keeps photos: the rows and files a <see cref="WallPhotoSlot"/> is built for.</summary>
public sealed partial class WallPhotoPrivacyService
{
    private static readonly string[] JournalledPhotoTypes = [nameof(Wall), nameof(WallPanel), nameof(WallReset)];

    private async Task<List<WallPhotoSlot>> CollectSlotsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var slots = new List<WallPhotoSlot>
        {
            Column<Wall>(d => d.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId), w => w.Photo, w => w.PhotoSha256),
            Column<Wall>(d => d.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId), w => w.StagedPhoto, w => w.StagedPhotoSha256),
        };

        // Every generation's panel rows: a superseded generation keeps its photo for the history views.
        var panelIds = await db.WallPanels.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.WallId == wallId).OrderBy(p => p.Generation).Select(p => p.Id).ToListAsync(ct);
        foreach (var id in panelIds)
        {
            slots.Add(Column<WallPanel>(d => d.WallPanels.IgnoreQueryFilters().Where(p => p.Id == id), p => p.Photo, p => p.PhotoSha256));
            slots.Add(Column<WallPanel>(
                d => d.WallPanels.IgnoreQueryFilters().Where(p => p.Id == id), p => p.StagedPhoto, p => p.StagedPhotoSha256));
        }

        var resetIds = await db.WallResets.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.WallId == wallId).Select(r => r.Id).ToListAsync(ct);
        slots.AddRange(resetIds.Select(id =>
            Column<WallReset>(d => d.WallResets.IgnoreQueryFilters().Where(r => r.Id == id), r => r.PreviousPhoto, r => r.PreviousPhotoSha256)));

        var images = await db.WallImages.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.WallId == wallId).Select(i => new { i.Id, i.StoragePath }).ToListAsync(ct);
        slots.AddRange(images.Select(i => GalleryFile(i.Id, i.StoragePath)));

        var shas = await JournalShasAsync(db, wallId, [.. panelIds, .. resetIds], ct);
        slots.AddRange(shas.Order(StringComparer.Ordinal).Select(JournalCopy));
        return slots;
    }

    /// <summary>
    /// A blob column on one row, replaced only while it still equals what was read; its content hash
    /// (<paramref name="hash"/>) moves in the same statement, since <c>ExecuteUpdate</c> bypasses the save-time stamp.
    /// </summary>
    private static WallPhotoSlot Column<T>(
        Func<BlocwerkDbContext, IQueryable<T>> rows, Expression<Func<T, byte[]?>> column, Expression<Func<T, byte[]?>> hash)
        where T : class => new(
            (db, ct) => rows(db).AsNoTracking().Select(column).FirstOrDefaultAsync(ct),
            async (db, original, clean, ct) => await rows(db)
                .Where(StillEquals(column, original))
                .ExecuteUpdateAsync(s => s.SetProperty(column, clean).SetProperty(hash, ContentHash.Of(clean)), ct) == 1);

    /// <summary><c>row => column == original</c>, with <paramref name="original"/> sent as a parameter, never inlined.</summary>
    private static Expression<Func<T, bool>> StillEquals<T>(Expression<Func<T, byte[]?>> column, byte[] original)
    {
        Expression<Func<byte[]>> captured = () => original;
        return Expression.Lambda<Func<T, bool>>(Expression.Equal(column.Body, captured.Body), column.Parameters);
    }

    /// <summary>
    /// A gallery upload on disk: replaced through a temp file and an atomic move, and its row's size
    /// and content hash updated — which also moves the image's variant-cache key, ETag and url version. Both are written
    /// in a transaction that commits only after the move, so a crash before the move changes nothing; a
    /// crash between the move and the commit leaves a clean file under the old size and hash, which
    /// <see cref="WallPhotoSlot.Repair"/> puts right on the next run (a rerun sees a clean file and would
    /// otherwise never touch the row again).
    /// </summary>
    private WallPhotoSlot GalleryFile(Guid imageId, string storedName)
    {
        var path = imageStorage.ResolvePhysicalPath(storedName);
        return new(
            async (_, ct) => path is not null && File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null,
            async (db, _, clean, ct) =>
            {
                var temp = imageStorage.CreateTempPath(Path.GetExtension(path!));
                await File.WriteAllBytesAsync(temp, clean, ct);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.WallImages.IgnoreQueryFilters().Where(i => i.Id == imageId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.SizeBytes, clean.LongLength).SetProperty(i => i.Sha256, ContentHash.Of(clean)), ct);
                File.Move(temp, path!, overwrite: true);
                await tx.CommitAsync(ct);
                return true;
            },
            async (db, apply, ct) =>
            {
                if (path is null || !File.Exists(path))
                {
                    return false;
                }

                var length = new FileInfo(path).Length;
                var drifted = db.WallImages.IgnoreQueryFilters().Where(i => i.Id == imageId && i.SizeBytes != length);
                if (!apply)
                {
                    return await drifted.AnyAsync(ct);
                }

                var sha = await ContentHash.OfFileAsync(path, ct);
                return await drifted.ExecuteUpdateAsync(s => s.SetProperty(i => i.SizeBytes, length).SetProperty(i => i.Sha256, sha), ct) > 0;
            });
    }

    /// <summary>
    /// A change-journal copy of a photo. Cleaned IN PLACE under its original content hash: the hash stays
    /// the key every journal entry references, so revert/export keep resolving it — to the clean bytes,
    /// which are also what the live row now holds. The key is therefore no longer the hash of the bytes.
    /// </summary>
    private static WallPhotoSlot JournalCopy(string sha) => new(
        (db, ct) => db.JournalBlobs.AsNoTracking().Where(b => b.Sha256 == sha).Select(b => (byte[]?)b.Bytes).FirstOrDefaultAsync(ct),
        async (db, original, clean, ct) => await db.JournalBlobs
            .Where(b => b.Sha256 == sha && b.Bytes == original)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Bytes, clean).SetProperty(b => b.Len, clean.LongLength), ct) == 1);

    /// <summary>
    /// The blobs referenced by journal entries of this wall's photo-carrying rows: the wall itself, its
    /// panels and resets, and rows since deleted (a discarded staged panel) — those are recognised by the
    /// wall id their own before/after image names.
    /// </summary>
    private static async Task<HashSet<string>> JournalShasAsync(
        BlocwerkDbContext db, Guid wallId, IEnumerable<Guid> ownedIds, CancellationToken ct)
    {
        var wallText = wallId.ToString();
        var entries = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => JournalledPhotoTypes.Contains(e.EntityType))
            .Select(e => new { e.KeyJson, e.BeforeJson, e.AfterJson })
            .ToListAsync(ct);

        var ids = new HashSet<string>(ownedIds.Select(i => i.ToString()), StringComparer.OrdinalIgnoreCase) { wallText };
        foreach (var e in entries.Where(e => Mentions(e.BeforeJson, wallText) || Mentions(e.AfterJson, wallText)))
        {
            if (KeyId(e.KeyJson) is { } id)
            {
                ids.Add(id);
            }
        }

        return entries
            .Where(e => KeyId(e.KeyJson) is { } id && ids.Contains(id))
            .SelectMany(e => ChangeJournalValueWriter.BlobShas(e.BeforeJson).Concat(ChangeJournalValueWriter.BlobShas(e.AfterJson)))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool Mentions(string? json, string wallText) =>
        json is not null && json.Contains(wallText, StringComparison.OrdinalIgnoreCase);

    private static string? KeyId(string keyJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(keyJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
