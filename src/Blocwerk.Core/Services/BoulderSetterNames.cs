using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Loading and formatting of a boulder's setter names. Kept in one place so the wall boulder list
/// and the boulder detail page render "set by" identically, and so the list can fetch just the
/// names instead of whole <c>User</c> rows.
/// </summary>
public static class BoulderSetterNames
{
    /// <summary>
    /// Every setter display name on the wall, keyed by boulder, in a single query. Boulders with no
    /// recorded setter (older data, or a boulder created without co-setters) are simply absent.
    /// </summary>
    public static async Task<Dictionary<Guid, List<string>>> LoadForWallAsync(
        BlocwerkDbContext db,
        Guid wallId)
    {
        var rows = await db.BoulderSetters
            .AsNoTracking()
            .Where(s => s.Boulder.WallId == wallId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new
            {
                s.BoulderId,
                s.User.CustomDisplayName,
                s.User.DisplayName
            })
            .ToListAsync();

        return rows
            .GroupBy(r => r.BoulderId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(r => string.IsNullOrWhiteSpace(r.CustomDisplayName)
                        ? r.DisplayName
                        : r.CustomDisplayName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList());
    }

    /// <summary>
    /// The names as a human list: "A", "A &amp; B", or "A, B &amp; C". Empty when there is no usable
    /// name, which is the caller's signal to fall back to the boulder's creator.
    /// </summary>
    public static string Format(IEnumerable<string?>? names)
    {
        var usable = names?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList() ?? [];

        if (usable.Count == 0)
        {
            return string.Empty;
        }

        if (usable.Count == 1)
        {
            return usable[0];
        }

        return string.Join(", ", usable.Take(usable.Count - 1)) + " & " + usable[^1];
    }
}
