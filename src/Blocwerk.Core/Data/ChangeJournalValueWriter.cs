using System.Security.Cryptography;
using System.Text.Json;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Data;

/// <summary>
/// Serializes and reconstitutes primary keys and before/after property images for the change journal.
/// byte[] values are never inlined into the JSON — they are content-hashed into a
/// <see cref="JournalBlob"/> and referenced as <c>{"$blob":"&lt;sha256&gt;","len":&lt;n&gt;}</c>,
/// deduplicated by content across rows. The read side (revert/replay) resolves those refs back to
/// bytes through a caller-supplied lookup.
/// </summary>
internal static class ChangeJournalValueWriter
{
    /// <summary>Serializes the entity's primary key as JSON, handling composite keys generically.</summary>
    public static string SerializeKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        var dict = new Dictionary<string, object?>();
        if (key is not null)
        {
            foreach (var property in key.Properties)
            {
                dict[property.Name] = entry.Property(property.Name).CurrentValue;
            }
        }

        return JsonSerializer.Serialize(dict);
    }

    /// <summary>
    /// Serializes the given properties' current or original values, hashing any byte[] to a blob ref.
    /// Discovered blobs are appended to <paramref name="pendingBlobs"/> (in-memory dedup only); the
    /// caller filters them against the store and adds the genuinely new ones — see
    /// <see cref="FilterNewBlobs"/> / <see cref="FilterNewBlobsAsync"/>.
    /// </summary>
    public static string Serialize(
        IReadOnlyList<PropertyEntry> properties,
        bool useCurrent,
        List<JournalBlob> pendingBlobs)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var property in properties)
        {
            var value = useCurrent ? property.CurrentValue : property.OriginalValue;
            dict[property.Metadata.Name] = value is byte[] bytes
                ? BlobReference(bytes, pendingBlobs)
                : value;
        }

        return JsonSerializer.Serialize(dict);
    }

    /// <summary>Sync existence filter: the blobs from <paramref name="pending"/> not already stored.</summary>
    public static List<JournalBlob> FilterNewBlobs(DbContext context, List<JournalBlob> pending)
    {
        if (pending.Count == 0)
        {
            return pending;
        }

        var shas = pending.Select(b => b.Sha256).ToList();
        var existing = context.Set<JournalBlob>()
            .Where(b => shas.Contains(b.Sha256))
            .Select(b => b.Sha256)
            .ToHashSet();
        return pending.Where(b => !existing.Contains(b.Sha256)).ToList();
    }

    /// <summary>Async existence filter — the version the async save path uses, avoiding a blocking query.</summary>
    public static async Task<List<JournalBlob>> FilterNewBlobsAsync(
        DbContext context, List<JournalBlob> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return pending;
        }

        var shas = pending.Select(b => b.Sha256).ToList();
        var existing = (await context.Set<JournalBlob>()
                .Where(b => shas.Contains(b.Sha256))
                .Select(b => b.Sha256)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        return pending.Where(b => !existing.Contains(b.Sha256)).ToList();
    }

    /// <summary>Reconstitutes a recorded before/after image into a name→value map for the entity type.</summary>
    /// <remarks>
    /// Only properties that still exist on the model are returned (an unknown/renamed column is
    /// skipped). Each value is coerced to the property's CLR type; a <c>{"$blob"}</c> ref is resolved
    /// back to bytes via <paramref name="resolveBlob"/> (which may return null when the blob is absent).
    /// </remarks>
    public static Dictionary<string, object?> DeserializeValues(
        IEntityType entityType, string json, Func<string, byte[]?> resolveBlob)
    {
        var result = new Dictionary<string, object?>();
        using var doc = JsonDocument.Parse(json);
        foreach (var member in doc.RootElement.EnumerateObject())
        {
            var property = entityType.FindProperty(member.Name);
            if (property is null)
            {
                continue;
            }

            result[member.Name] = ConvertElement(member.Value, property.ClrType, resolveBlob);
        }

        return result;
    }

    /// <summary>Reconstitutes a <c>KeyJson</c> into key values ordered as the primary key expects.</summary>
    public static object?[] DeserializeKey(IEntityType entityType, string keyJson)
    {
        var key = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{entityType.ClrType.Name} has no primary key.");

        using var doc = JsonDocument.Parse(keyJson);
        var root = doc.RootElement;
        var values = new object?[key.Properties.Count];
        for (var i = 0; i < key.Properties.Count; i++)
        {
            var property = key.Properties[i];
            values[i] = root.TryGetProperty(property.Name, out var element)
                ? ConvertElement(element, property.ClrType, _ => null)
                : null;
        }

        return values;
    }

    /// <summary>The distinct blob shas referenced by a before/after image (empty for null/plain JSON).</summary>
    public static IEnumerable<string> BlobShas(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            yield break;
        }

        using var doc = JsonDocument.Parse(json);
        foreach (var member in doc.RootElement.EnumerateObject())
        {
            if (member.Value.ValueKind == JsonValueKind.Object
                && member.Value.TryGetProperty("$blob", out var sha)
                && sha.GetString() is { } value)
            {
                yield return value;
            }
        }
    }

    private static object? ConvertElement(JsonElement element, Type targetType, Func<string, byte[]?> resolveBlob)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$blob", out var sha))
        {
            return sha.GetString() is { } value ? resolveBlob(value) : null;
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return element.Deserialize(underlying);
    }

    private static object BlobReference(byte[] bytes, List<JournalBlob> pendingBlobs)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!pendingBlobs.Any(b => b.Sha256 == sha))
        {
            pendingBlobs.Add(new JournalBlob { Sha256 = sha, Bytes = bytes, Len = bytes.Length });
        }

        return new Dictionary<string, object?>
        {
            ["$blob"] = sha,
            ["len"] = (long)bytes.Length,
        };
    }
}
