using System.Globalization;

namespace Blocwerk.Web.Components.Shared;

/// <summary>Display formatting for the printed-marker (glyph) settings: one decimal, a dash for unknown.</summary>
internal static class GlyphFormat
{
    private const string Unknown = "—";

    /// <summary>An angle in degrees, e.g. "44.6°".</summary>
    public static string Degrees(double? value) =>
        value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + "°" : Unknown;

    /// <summary>A reprojection error in pixels, e.g. "0.90 px".</summary>
    public static string Pixels(double? value) =>
        value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) + " px" : Unknown;

    /// <summary>A millimetre length shown in metres, e.g. "3.10 m".</summary>
    public static string Metres(double? millimetres) =>
        millimetres is { } v ? (v / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " m" : Unknown;

    /// <summary>A marker size, e.g. "125 mm".</summary>
    public static string Millimetres(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
}
