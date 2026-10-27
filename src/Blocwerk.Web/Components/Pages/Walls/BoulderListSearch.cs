// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The boulder list's free-text search: matches what a row shows, its name, grade and setters (or creator), ignoring
/// case and accents, so "eclair" finds "Éclair". Every word of the query has to match somewhere in the row, in any
/// order, so "6a crimp" finds the 6A called "Crimp line".
/// </summary>
internal static class BoulderListSearch
{
    /// <summary>The query's words, folded; empty when there is nothing to search for.</summary>
    public static string[] Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether <paramref name="item"/> matches every one of <paramref name="terms"/>.</summary>
    public static bool Matches(BoulderListItem item, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return true;
        }

        var haystack = Fold(string.Join('\n', item.Boulder.Name, item.Boulder.Grade ?? string.Empty, item.AuthorDisplay));
        foreach (var term in terms)
        {
            if (!haystack.Contains(term, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lower case with diacritics stripped: "Éclair" and "ECLAIR" both become "eclair". Letters that are not a base
    /// letter plus a mark are spelled out the way a keyboard without them would type them: "Großer" is "grosser",
    /// "Łódź" is "lodz".
    /// </summary>
    public static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c switch
            {
                'ß' => "ss",
                'ł' => "l",
                'ø' => "o",
                'æ' => "ae",
                'đ' => "d",
                'œ' => "oe",
                _ => c.ToString(),
            });
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
