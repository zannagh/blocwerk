// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Blocwerk.Core.Services;

/// <summary>
/// The <c>?v=</c> of a byte URL that may be cached <c>immutable</c>. Some assets keep their URL while
/// their bytes change (a splat re-run swaps the row under the same model id, the privacy pass rewrites
/// a gallery file in place), so "immutable" is only true of a URL that also names the bytes. The client
/// is handed URLs carrying the current version; the route serves them immutable only while that version
/// still matches, and anything else (an old unversioned URL, a stale version) with revalidation.
/// </summary>
public static class AssetVersion
{
    /// <summary>The query parameter the version rides in.</summary>
    public const string QueryName = "v";

    /// <summary>The unit separator, used only to keep the hashed parts from running together.</summary>
    private const char PartSeparator = '\u001f';

    /// <summary>
    /// An opaque, fixed-size hash (32 hex chars) over the parts that identify the stored bytes. The same
    /// derivation backs the image routes' ETags, so a version and its ETag can never disagree.
    /// </summary>
    public static string Of(params object?[] parts)
    {
        // Formatted invariantly, never under the ambient culture: a culture-sensitive part (a date, a
        // decimal) would otherwise make the value depend on the server's locale and silently invalidate
        // every browser and disk cache the moment that changed.
        var raw = string.Join(
            PartSeparator,
            parts.Select(p => p is IFormattable f
                ? f.ToString(null, CultureInfo.InvariantCulture)
                : p?.ToString() ?? string.Empty));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary><paramref name="url"/> with <c>v=<paramref name="version"/></c> appended to its query.</summary>
    public static string Append(string url, string version) =>
        url + (url.Contains('?') ? '&' : '?') + QueryName + "=" + version;

    /// <summary>Whether the requested version names exactly the bytes stored now.</summary>
    public static bool Matches(string? requested, string current) =>
        !string.IsNullOrEmpty(requested) && string.Equals(requested, current, StringComparison.Ordinal);
}
