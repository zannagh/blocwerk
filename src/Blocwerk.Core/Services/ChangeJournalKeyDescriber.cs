using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Turns a conflict's raw key JSON into something an operator can read. Deliberately CHEAP: it parses
/// the key locally and issues at most two extra queries (walls, boulders) for the whole conflict set —
/// no per-conflict lookups, no joins for entity types that have no name anyway.
/// </summary>
internal static class ChangeJournalKeyDescriber
{
    public static async Task<IReadOnlyList<ChangeJournalConflictInfo>> DescribeAsync(
        BlocwerkDbContext db, IReadOnlyList<ChangeJournalConflict> conflicts, CancellationToken ct)
    {
        if (conflicts.Count == 0)
        {
            return [];
        }

        // IgnoreQueryFilters on walls: this is an operator view of the journal, and the caller (the
        // admin panel) is responsible for authorising it — the membership filter would otherwise blank
        // out names for walls the acting admin is not a member of.
        var names = new Dictionary<Guid, string>();
        var wallIds = IdsOf(conflicts, nameof(Wall));
        var boulderIds = IdsOf(conflicts, nameof(Boulder));
        if (wallIds.Count > 0)
        {
            var rows = await db.Walls.IgnoreQueryFilters()
                .Where(w => wallIds.Contains(w.Id)).Select(w => new { w.Id, w.Name }).ToListAsync(ct);
            foreach (var row in rows)
            {
                names[row.Id] = row.Name;
            }
        }

        if (boulderIds.Count > 0)
        {
            var rows = await db.Boulders.IgnoreQueryFilters()
                .Where(b => boulderIds.Contains(b.Id)).Select(b => new { b.Id, b.Name }).ToListAsync(ct);
            foreach (var row in rows)
            {
                names[row.Id] = row.Name;
            }
        }

        return conflicts
            .Select(c =>
            {
                var single = SingleId(c.KeyJson);
                var name = single is not null && names.TryGetValue(single.Value, out var found) ? found : null;
                return new ChangeJournalConflictInfo(c, ShortKey(c.KeyJson), name);
            })
            .ToList();
    }

    /// <summary>Renders <c>{"Id":"4f2a…"}</c> as <c>Id=4f2a1b9c</c>, composite keys comma-joined.</summary>
    private static string ShortKey(string keyJson)
    {
        var parts = Parse(keyJson).Select(p => $"{p.Key}={Shorten(p.Value)}").ToList();
        return parts.Count == 0 ? keyJson : string.Join(", ", parts);
    }

    private static Guid? SingleId(string keyJson)
    {
        var parts = Parse(keyJson);
        return parts.Count == 1 && Guid.TryParse(parts[0].Value, out var id) ? id : null;
    }

    private static List<Guid> IdsOf(IEnumerable<ChangeJournalConflict> conflicts, string entityType) =>
        conflicts
            .Where(c => c.EntityType == entityType)
            .Select(c => SingleId(c.KeyJson))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

    private static List<KeyValuePair<string, string>> Parse(string keyJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(keyJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return doc.RootElement
                .EnumerateObject()
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ToString()))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Shorten(string value) =>
        Guid.TryParse(value, out var id) ? id.ToString("N")[..8] : value;
}
