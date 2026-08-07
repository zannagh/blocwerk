using System.Globalization;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Maps a TopLogger grade to a Font label our <see cref="GradeScoring"/> understands. TopLogger
/// encodes a boulder grade as a decimal number whose integer part is the number and whose fraction
/// is the A / A+ / B / B+ / C / C+ sub-grade in sixths (6.0 = 6A, 6.17 = 6A+, … 6.83 = 6C+, 7.0 = 7A).
/// Already-formatted Font ("6C") or V ("V5") labels are accepted directly. Returns null when it can't
/// be mapped — the ascent still imports, it just won't score until the mapping is refined.
///
/// NOTE: validated against the documented convention, not live data yet — confirm on first real sync.
/// </summary>
public static class TopLoggerGradeMapper
{
    private static readonly string[] SubGrades = ["A", "A+", "B", "B+", "C", "C+"];

    public static string? ToFontGrade(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var upper = value.ToUpperInvariant();

        // Already a Font label (e.g. "6C", "7A+"). Check Font membership directly — GetScore also
        // accepts V-grades (it converts them), so it can't distinguish the two systems here.
        if (GradeScoring.AllScores.ContainsKey(upper))
        {
            return upper;
        }

        // A V-scale label (e.g. "V5").
        var fromV = GradeScale.ToFont(upper);
        if (!string.IsNullOrEmpty(fromV))
        {
            return fromV;
        }

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return DecimalToFont(number);
        }

        return null;
    }

    private static string? DecimalToFont(double grade)
    {
        if (grade <= 0)
        {
            return null;
        }

        var whole = (int)Math.Floor(grade);
        var fraction = grade - whole;

        // Grades below 6 are plain ("5", "5+", "4"…) rather than lettered.
        if (whole < 6)
        {
            var plain = fraction >= 0.25 ? $"{whole}+" : whole.ToString(CultureInfo.InvariantCulture);
            return GradeScoring.GetScore(plain, false) > 0 ? plain : null;
        }

        var sub = (int)Math.Round(fraction * 6);
        if (sub >= SubGrades.Length)
        {
            whole += 1;
            sub = 0;
        }

        var font = $"{whole}{SubGrades[sub]}";
        return GradeScoring.GetScore(font, false) > 0 ? font : null;
    }
}
