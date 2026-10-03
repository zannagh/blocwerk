using System.Linq.Expressions;
using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>One stored photo: how to read it, and how to replace it only if it still holds the bytes that were read.</summary>
/// <param name="Load">Reads the photo, or null when the slot is empty.</param>
/// <param name="Save">Writes (db, original, clean); false when the stored bytes changed since they were read.</param>
internal sealed record WallPhotoSlot(
    Func<BlocwerkDbContext, CancellationToken, Task<byte[]?>> Load,
    Func<BlocwerkDbContext, byte[], byte[], CancellationToken, Task<bool>> Save);

/// <summary>Where a wall keeps photos: the rows and files a <see cref="WallPhotoSlot"/> is built for.</summary>
public sealed partial class WallPhotoPrivacyService
{
    private static readonly string[] JournalledPhotoTypes = [nameof(Wall), nameof(WallPanel), nameof(WallReset)];

    private async Task<List<WallPhotoSlot>> CollectSlotsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var slots = new List<WallPhotoSlot>
        {
            Column<Wall>(d => d.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId), w => w.Photo),
            Column<Wall>(d => d.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId), w => w.StagedPhoto),
        };

        // Every generation's panel rows: a superseded generation keeps its photo for the history views.
        var panelIds = await db.WallPanels.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.WallId == wallId).OrderBy(p => p.Generation).Select(p => p.Id).ToListAsync(ct);
        foreach (var id in panelIds)
        {
            slots.Add(Column<WallPanel>(d => d.WallPanels.IgnoreQueryFilters().Where(p => p.Id == id), p => p.Photo));
            slots.Add(Column<WallPanel>(d => d.WallPanels.IgnoreQueryFilters().Where(p => p.Id == id), p => p.StagedPhoto));
        }

        var resetIds = await db.WallResets.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.WallId == wallId).Select(r => r.Id).ToListAsync(ct);
        slots.AddRange(resetIds.Select(id =>
            Column<WallReset>(d => d.WallResets.IgnoreQueryFilters().Where(r => r.Id == id), r => r.PreviousPhoto)));

        var images = await db.WallImages.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.WallId == wallId).Select(i => new { i.Id, i.StoragePath }).ToListAsync(ct);
        slots.AddRange(images.Select(i => GalleryFile(i.Id, i.StoragePath)));

        var shas = await JournalShasAsync(db, wallId, [.. panelIds, .. resetIds], ct);
        slots.AddRange(shas.Order(StringComparer.Ordinal).Select(JournalCopy));
        return slots;
    }

    /// <summary>A blob column on one row, replaced only while it still equals what was read.</summary>
    private static WallPhotoSlot Column<T>(Func<BlocwerkDbContext, IQueryable<T>> rows, Expression<Func<T, byte[]?>> column)
        where T : class => new(
            (db, ct) => rows(db).AsNoTracking().Select(column).FirstOrDefaultAsync(ct),
            async (db, original, clean, ct) => await rows(db)
                .Where(StillEquals(column, original))
                .ExecuteUpdateAsync(s => s.SetProperty(column, clean), ct) == 1);

    /// <summary><c>row => column == original</c>, with <paramref name="original"/> sent as a parameter, never inlined.</summary>
    private static Expression<Func<T, bool>> StillEquals<T>(Expression<Func<T, byte[]?>> column, byte[] original)
    {
        Expression<Func<byte[]>> captured = () => original;
        return Expression.Lambda<Func<T, bool>>(Expression.Equal(column.Body, captured.Body), column.Parameters);
    }

    /// <summary>
    /// A gallery upload on disk: replaced through a temp file and an atomic move, and its row's size
    /// updated — which also moves the image's variant-cache key and ETag.
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
                File.Move(temp, path!, overwrite: true);
                await db.WallImages.IgnoreQueryFilters().Where(i => i.Id == imageId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.SizeBytes, clean.LongLength), ct);
                return true;
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
