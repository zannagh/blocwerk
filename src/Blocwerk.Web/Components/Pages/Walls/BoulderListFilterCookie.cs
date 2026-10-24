// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The boulder list's filters and sort, remembered per wall in one cookie (written by <c>bwPrefs.setBoulderFilters</c> in
/// <c>wwwroot/js/prefs.js</c>), so they survive a reload and an app restart. Per wall because that is the scope the list
/// already had: a wall's filters were never carried to another wall's list.
/// <para>
/// The free-text search is deliberately not part of it: it is a transient lookup, not a setting.
/// </para>
/// <para>
/// Format: entries separated by <c>|</c>, most recently used first, each <c>wallId,flags,sort,gradeMin,gradeMax</c>, with
/// the wall id as 32 hex digits and the grades URI-escaped (so neither separator can appear inside one). At most
/// <see cref="MaxWalls"/> entries, and a wall whose filters are all at their defaults is dropped instead of stored, so the
/// cookie stays a few hundred bytes. Anything malformed is skipped, never thrown on.
/// </para>
/// </summary>
internal static class BoulderListFilterCookie
{
    /// <summary>The cookie prefs.js writes. Also listed on the privacy page.</summary>
    public const string CookieName = "blocwerk-boulder-filters";

    /// <summary>How many walls the cookie remembers; the least recently used one falls out first.</summary>
    public const int MaxWalls = 12;

    private const int MaxGradeLength = 12;

    private const int MaxSort = 31;

    /// <summary>The filters stored for <paramref name="wallId"/>, or null when there are none.</summary>
    public static WallListFilter? Read(string? stored, Guid wallId)
    {
        foreach (var (id, filter) in Parse(stored))
        {
            if (id == wallId)
            {
                return filter;
            }
        }

        return null;
    }

    /// <summary>
    /// The cookie value with <paramref name="wallId"/>'s filters stored (search text ignored) and moved to the front, or
    /// removed when they are all defaults.
    /// </summary>
    public static string Write(string? stored, Guid wallId, WallListFilter filter)
    {
        var entries = Parse(stored).Where(e => e.WallId != wallId).ToList();
        var normalized = Normalize(filter);
        if (!IsDefault(normalized))
        {
            entries.Insert(0, (wallId, normalized));
        }

        var builder = new StringBuilder();
        foreach (var (id, f) in entries.Take(MaxWalls))
        {
            if (builder.Length > 0)
            {
                builder.Append('|');
            }

            builder.Append(CultureInfo.InvariantCulture, $"{id:N},{Flags(f)},{f.Sort},{Escape(f.GradeMin)},{Escape(f.GradeMax)}");
        }

        return builder.ToString();
    }

    /// <summary>The part of <paramref name="filter"/> the cookie keeps: everything but the search text.</summary>
    public static WallListFilter Normalize(WallListFilter filter) => filter with
    {
        SearchText = string.Empty,
        GradeMin = string.IsNullOrEmpty(filter.GradeMin) ? null : filter.GradeMin,
        GradeMax = string.IsNullOrEmpty(filter.GradeMax) ? null : filter.GradeMax,
    };

    private static bool IsDefault(WallListFilter f) =>
        f.GradeMin is null && f.GradeMax is null && Flags(f) == 0 && f.Sort == 0;

    private static int Flags(WallListFilter f) =>
        (f.Attempted ? 1 : 0) | (f.Done ? 2 : 0) | (f.NotDone ? 4 : 0) | (f.Favorites ? 8 : 0)
        | (f.Created ? 16 : 0) | (f.Beta ? 32 : 0) | (f.NeedsReview ? 64 : 0);

    private static string Escape(string? grade) => grade is null ? string.Empty : Uri.EscapeDataString(grade);

    private static IEnumerable<(Guid WallId, WallListFilter Filter)> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            yield break;
        }

        foreach (var entry in stored.Split('|', StringSplitOptions.RemoveEmptyEntries).Take(MaxWalls))
        {
            if (TryParseEntry(entry, out var wallId, out var filter))
            {
                yield return (wallId, filter);
            }
        }
    }

    private static bool TryParseEntry(string entry, out Guid wallId, out WallListFilter filter)
    {
        filter = null!;
        var parts = entry.Split(',');
        if (parts.Length != 5
            || !Guid.TryParseExact(parts[0], "N", out wallId)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var flags)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sort)
            || sort > MaxSort
            || !TryUnescape(parts[3], out var min)
            || !TryUnescape(parts[4], out var max))
        {
            wallId = Guid.Empty;
            return false;
        }

        filter = new WallListFilter(
            string.Empty,
            min,
            max,
            Attempted: (flags & 1) != 0,
            Done: (flags & 2) != 0,
            NotDone: (flags & 4) != 0,
            Favorites: (flags & 8) != 0,
            Created: (flags & 16) != 0,
            Beta: (flags & 32) != 0,
            NeedsReview: (flags & 64) != 0,
            sort);
        return true;
    }

    private static bool TryUnescape(string raw, out string? grade)
    {
        grade = null;
        if (raw.Length == 0)
        {
            return true;
        }

        try
        {
            grade = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return grade.Length <= MaxGradeLength;
    }
}
