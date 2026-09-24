using System.Globalization;
using System.Text.Json;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>Parsing and validation of what the admin types into the declarations table.</summary>
public static class CaptureDeclarationRules
{
    /// <summary>Highest marker id without a plan (<c>segment*6 + role</c>); a plan's layout says its own.</summary>
    public const int MaxMarkerId = WallMarkerLayout.LegacyMaxMarkerId;

    /// <summary>
    /// Parses "14-15, 8-9" (also ";" / whitespace separated) into marker id pairs. Returns an error
    /// message instead of throwing.
    /// </summary>
    public static (IReadOnlyList<int[]> Pairs, string? Error) ParseLevelPairs(string? text, int maxMarkerId = MaxMarkerId)
    {
        var pairs = new List<int[]>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (pairs, null);
        }

        foreach (var token in text.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b)
                || a == b || a > maxMarkerId || b > maxMarkerId)
            {
                return ([], $"\"{token}\" is not a pair of two different marker ids (0–{maxMarkerId}), e.g. 14-15.");
            }

            pairs.Add([a, b]);
        }

        return (pairs, null);
    }

    /// <summary>Marker ids as compact runs, e.g. "0–5, 24–27" (en dash, as the declarations table shows them).</summary>
    public static string FormatIdRanges(IEnumerable<int> ids)
    {
        var sorted = ids.Distinct().Order().ToList();
        var runs = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }

            runs.Add(j == i ? $"{sorted[i]}" : $"{sorted[i]}–{sorted[j]}");
            i = j + 1;
        }

        return string.Join(", ", runs);
    }

    public static string FormatLevelPairs(IEnumerable<int[]> pairs) =>
        string.Join(", ", pairs.Where(p => p.Length == 2).Select(p => $"{p[0]}-{p[1]}"));

    /// <summary>
    /// Problems that make a declaration set unusable, as admin-facing messages. <paramref name="seenSegments"/>
    /// is null while photos still wait for detection (the pipeline detects them), which skips that check.
    /// </summary>
    public static IReadOnlyList<string> Validate(CaptureDeclarations declarations, ISet<int>? seenSegments)
    {
        var errors = new List<string>();
        foreach (var s in declarations.Segments)
        {
            if (s.DeclaredAngleDeg is { } angle && (!double.IsFinite(angle) || angle < -90 || angle > 90))
            {
                errors.Add($"Segment {s.Index}: the angle must be between -90° and 90°.");
            }

            if (s.Name is null || s.Name.Length > 100)
            {
                errors.Add(s.Name is null ? $"Segment {s.Index}: a name is required." : $"Segment {s.Index}: the name is too long.");
            }
        }

        // The UI parses pairs itself (ParseLevelPairs); the API sends them as arrays, so their shape is checked here.
        if (declarations.LevelPairs.Any(p => p is null || p.Length != 2 || p[0] == p[1] || p[0] < 0 || p[1] < 0))
        {
            errors.Add("Each level pair must name two different marker ids, e.g. [14, 15].");
        }

        if (declarations.Segments.GroupBy(s => s.Index).Any(g => g.Count() > 1))
        {
            errors.Add("A segment is declared twice.");
        }

        if (seenSegments is not null && !declarations.Segments.Any(s => seenSegments.Contains(s.Index)))
        {
            errors.Add("None of the declared segments appears in the photos.");
        }

        return errors;
    }

    /// <summary>
    /// Rows the admin named but gave neither an angle nor the gravity flag. The solver never hears about such a
    /// segment (see <c>CaptureComputeDocuments.BuildSolveRequest</c>), so its markers are merged into the wall face
    /// they are coplanar with — right for spare filler markers, a surprise for a surface the admin meant as its own
    /// face. Not an error: the capture still starts; the admin is told what will happen.
    /// </summary>
    /// <param name="declarations">The declarations.</param>
    /// <returns>One warning per such row.</returns>
    public static IReadOnlyList<string> MergeWarnings(CaptureDeclarations declarations) => declarations.Segments
        .Where(WillMerge)
        .OrderBy(s => s.Index)
        .Select(MergeWarning)
        .ToList();

    /// <summary>The warning for one row, or null when it declares something (or is an unnamed spare row).</summary>
    /// <param name="segment">The row.</param>
    /// <returns>The warning, or null.</returns>
    public static string? MergeWarningFor(CaptureSegmentDeclaration segment) => WillMerge(segment) ? MergeWarning(segment) : null;

    /// <summary>The name a row gets when the admin names nothing ("Segment 4").</summary>
    /// <param name="index">The segment index.</param>
    /// <returns>The name.</returns>
    public static string DefaultName(int index) => string.Create(CultureInfo.InvariantCulture, $"Segment {index}");

    public static string Serialize(CaptureDeclarations declarations) => JsonSerializer.Serialize(declarations);

    public static CaptureDeclarations Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CaptureDeclarations.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<CaptureDeclarations>(json) ?? CaptureDeclarations.Empty;
        }
        catch (JsonException)
        {
            return CaptureDeclarations.Empty;
        }
    }

    private static bool WillMerge(CaptureSegmentDeclaration s) =>
        s.DeclaredAngleDeg is null && !s.VerticalReference
        && !string.IsNullOrWhiteSpace(s.Name) && s.Name.Trim() != DefaultName(s.Index);

    private static string MergeWarning(CaptureSegmentDeclaration s) =>
        $"Segment {s.Index} (“{s.Name.Trim()}”) has no angle, so its markers will be merged into the nearest wall face. "
        + "Give it an angle (or tick “gravity reference”) to keep it as a face of its own.";
}
