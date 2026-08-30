using System.Globalization;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Converts TopLogger scaled-integer grades into a best-effort Font label.
/// </summary>
/// <remarks>
/// This mapping is <b>approximate and heuristic</b>. TopLogger's GraphQL
/// introspection is disabled and the exact grade lookup tables are not
/// published, so the conversion is inferred from sampled data: the hundreds
/// component is the grade number and the remainder maps to a/b/c thirds
/// (e.g. <c>600</c> → <c>6A</c>, <c>633</c> → <c>6B</c>).
/// <para>
/// When a scaled grade cannot be confidently mapped (out of the Font range, or a
/// V-scale / unknown-system value that does not follow the scaled pattern), the
/// formatter returns <c>null</c> so the caller can flag the tick as needing a
/// manual grade mapping rather than showing a misleading label.
/// </para>
/// </remarks>
public static class GradeFormatter
{
    /// <summary>
    /// Attempts to format a scaled TopLogger grade as a Font label.
    /// </summary>
    /// <param name="grade">The scaled integer grade (e.g. 600, 633, 700), or <c>null</c>.</param>
    /// <returns>A Font label such as <c>6A</c>, or <c>null</c> when it cannot be mapped.</returns>
    public static string? ToFontGrade(long? grade)
    {
        if (grade is not { } value)
        {
            return null;
        }

        // Scaled Font grades run roughly 100 (grade 1) to 999 (grade 9). Values
        // outside that window are almost certainly a different scale (e.g. a raw
        // V-scale number) and cannot be mapped to Font.
        if (value < 100 || value >= 1000)
        {
            return null;
        }

        long number = value / 100;
        long remainder = value % 100;
        string third = remainder switch
        {
            <= 32 => "A",
            <= 66 => "B",
            _ => "C",
        };

        return string.Concat(number.ToString(CultureInfo.InvariantCulture), third);
    }

    /// <summary>
    /// Attempts to format a raw grade string (as returned by the API, which may be
    /// a scaled number like <c>"600"</c>) as a Font label. Non-numeric or
    /// unmappable values return <c>null</c>.
    /// </summary>
    public static string? ToFontGrade(string? rawGrade)
    {
        if (string.IsNullOrWhiteSpace(rawGrade))
        {
            return null;
        }

        return long.TryParse(rawGrade, NumberStyles.Any, CultureInfo.InvariantCulture, out long scaled)
            ? ToFontGrade(scaled)
            : null;
    }
}
