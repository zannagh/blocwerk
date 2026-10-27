// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.WhatsNew;

/// <summary>
/// Just enough SemVer to compare the running build with a release tag: <c>v0.1.0</c>, <c>0.1.1-224</c>,
/// <c>0.2.0-ci.5</c>. Not a general SemVer implementation.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>The tag or version without a leading v and without <c>+build</c> metadata.</summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        return text.StartsWith('v') || text.StartsWith('V') ? text[1..] : text;
    }

    /// <summary>True when the two name the same version, so <c>v0.1.0</c> matches <c>0.1.0</c>.</summary>
    public static bool SameVersion(string? a, string? b)
    {
        var left = Normalize(a);
        return left.Length > 0 && string.Equals(left, Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the value parses and carries a prerelease label (<c>0.1.1-224</c>).</summary>
    public static bool IsPrerelease(string? value)
    {
        return TryParse(value, out _, out var label) && label.Length > 0;
    }

    /// <summary>
    /// Compares two versions: negative when <paramref name="a"/> is older. Null when either does not
    /// parse (<c>dev</c>), because then there is no honest answer.
    /// </summary>
    public static int? Compare(string? a, string? b)
    {
        if (!TryParse(a, out var coreA, out var labelA) || !TryParse(b, out var coreB, out var labelB))
        {
            return null;
        }

        var core = coreA.CompareTo(coreB);
        if (core != 0)
        {
            return core;
        }

        // Same core: the release outranks any prerelease of it.
        if (labelA.Length == 0 && labelB.Length == 0)
        {
            return 0;
        }

        if (labelA.Length == 0 || labelB.Length == 0)
        {
            return labelA.Length == 0 ? 1 : -1;
        }

        return CompareLabels(labelA, labelB);
    }

    private static bool TryParse(string? value, out Version core, out string label)
    {
        var text = Normalize(value);
        var dash = text.IndexOf('-');
        label = dash >= 0 ? text[(dash + 1)..] : string.Empty;
        var corePart = dash >= 0 ? text[..dash] : text;
        if (corePart.Count(c => c == '.') != 2 || !Version.TryParse(corePart, out var parsed))
        {
            core = new Version();
            return false;
        }

        core = parsed;
        return true;
    }

    private static int CompareLabels(string a, string b)
    {
        var partsA = a.Split('.');
        var partsB = b.Split('.');
        for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            var bothNumbers = long.TryParse(partsA[i], out var numberA) & long.TryParse(partsB[i], out var numberB);
            var result = bothNumbers
                ? numberA.CompareTo(numberB)
                : string.CompareOrdinal(partsA[i], partsB[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return partsA.Length.CompareTo(partsB.Length);
    }
}
