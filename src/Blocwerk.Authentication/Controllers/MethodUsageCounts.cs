namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// Parses, increments, and re-serializes the per-provider sign-in counter stored in the
/// <c>bw_method_counts</c> cookie. The cookie encodes counts as a compact, cookie-safe string such
/// as <c>github:2|google:1</c>; each count is capped so the value can never grow unbounded. Parsing
/// is fail-safe: any malformed input yields an empty set rather than throwing.
/// </summary>
internal static class MethodUsageCounts
{
    /// <summary>Maximum value any single provider count is allowed to reach.</summary>
    internal const int MaxCount = 9;

    /// <summary>
    /// Parses the compact cookie value into a provider to count map. Unknown, empty, or malformed
    /// entries are skipped; a null or blank input yields an empty map.
    /// </summary>
    internal static Dictionary<string, int> Parse(string? raw)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return counts;
        }

        foreach (var entry in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                continue;
            }

            var provider = entry[..separator];
            if (!IsSafeProvider(provider))
            {
                continue;
            }

            if (!int.TryParse(entry[(separator + 1)..], out var value) || value < 0)
            {
                continue;
            }

            counts[provider] = Math.Min(value, MaxCount);
        }

        return counts;
    }

    /// <summary>
    /// Increments the count for <paramref name="provider"/> by one (capped at <see cref="MaxCount"/>)
    /// and returns the new value.
    /// </summary>
    internal static int Increment(Dictionary<string, int> counts, string provider)
    {
        counts.TryGetValue(provider, out var current);
        var next = Math.Min(current + 1, MaxCount);
        counts[provider] = next;
        return next;
    }

    /// <summary>Serializes a provider to count map back into the compact cookie value.</summary>
    internal static string Serialize(Dictionary<string, int> counts)
    {
        return string.Join('|', counts
            .Where(kvp => IsSafeProvider(kvp.Key) && kvp.Value > 0)
            .Select(kvp => $"{kvp.Key}:{Math.Min(kvp.Value, MaxCount)}"));
    }

    // Only alphanumeric provider tokens are ever written or trusted, so a crafted cookie can never
    // inject separators or oversized junk back into the value.
    private static bool IsSafeProvider(string provider)
    {
        if (string.IsNullOrEmpty(provider))
        {
            return false;
        }

        foreach (var c in provider)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
