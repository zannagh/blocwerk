using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
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

    /// <summary>
    /// Who a boulder is credited to, everywhere it is shown: the setter(s) when any are recorded,
    /// otherwise whoever added it, otherwise <see cref="PlaceholderIdentity.DisplayName"/>.
    /// </summary>
    /// <remarks>
    /// The ONE formatter for a byline — the wall list's <c>AuthorDisplay</c> and the boulder detail
    /// page both call it, so "by X" and "set by X" can never disagree. It also never leaks a system
    /// row's raw name: a boulder set at an unattended kiosk has the Ghost row as its creator and
    /// renders as the placeholder, exactly like a boulder whose setter later deleted their account
    /// and like one that never recorded a setter at all. Three different situations, one word.
    /// </remarks>
    public static string Describe(IEnumerable<string?>? setterNames, User? creator)
    {
        var setters = Format(setterNames);
        if (!string.IsNullOrEmpty(setters))
        {
            return setters;
        }

        if (creator is null || GhostUser.Is(creator.Id) || string.IsNullOrWhiteSpace(creator.Name))
        {
            return PlaceholderIdentity.DisplayName;
        }

        return creator.Name;
    }
}
