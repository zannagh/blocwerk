using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Holds;

/// <summary>
/// The single source of truth for hold colors: the vocabulary stored in
/// <see cref="Entities.Hold.Color"/>, its display names, and the hex values every
/// renderer derives fills and strokes from.
/// </summary>
public static class HoldPalette
{
    /// <summary>Colors available for plastic holds (PU, PE, DualTex).</summary>
    public static readonly IReadOnlyList<HoldColor> PlasticColors =
    [
        new("yellow", "Yellow", "#e6c800", "#C6A700"),
        new("red", "Red", "#cc3333", "#B71C1C"),
        new("pink", "Pink", "#e05590", "#AD1457"),
        new("salmon", "Salmon", "#f08a70", "#c1543c"),
        new("orange", "Orange", "#dd8800", "#E65100"),
        new("blue", "Blue", "#3366dd", "#0D47A1"),
        new("green", "Green", "#33aa44", "#1B5E20"),
        new("purple", "Purple", "#8833bb", "#4A148C"),
        new("white", "White", "#eeeeee", "#BDBDBD"),
        new("gray", "Gray", "#9aa0a6", "#5f6368"),
        new("black", "Black", "#444444", "#212121"),
    ];

    /// <summary>The three brown tones a wooden hold can have.</summary>
    public static readonly IReadOnlyList<HoldColor> WoodColors =
    [
        new("wood-light", "Light wood", "#d2a679", "#a97c50"),
        new("wood-medium", "Medium wood", "#a0703c", "#7a5228"),
        new("wood-dark", "Dark wood", "#6b4423", "#452b14"),
    ];

    /// <summary>Every known color, plastic and wood.</summary>
    public static readonly IReadOnlyList<HoldColor> All = [.. PlasticColors, .. WoodColors];

    private static readonly Dictionary<string, HoldColor> ByKey =
        All.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback rendering for a hold with no color, or an unrecognised one.</summary>
    public static readonly HoldColor Unknown = new("none", "No color", "#e94560", "#C62828");

    /// <summary>
    /// The colors selectable for the given material. Wooden holds are restricted to
    /// brown tones; a null material (legacy holds) may use anything.
    /// </summary>
    public static IReadOnlyList<HoldColor> ColorsFor(HoldMaterial? material) =>
        material == HoldMaterial.Wood ? WoodColors : PlasticColors;

    /// <summary>
    /// True when <paramref name="colorKey"/> is a valid choice for the material.
    /// A null or empty color ("no color") is always allowed.
    /// </summary>
    public static bool IsValidFor(string? colorKey, HoldMaterial? material) =>
        string.IsNullOrEmpty(colorKey) || ColorsFor(material).Any(c => c.Key == colorKey);

    /// <summary>
    /// Coerces a color to one the material allows, returning null when the current
    /// color has to be dropped (e.g. switching a blue hold to wood).
    /// </summary>
    public static string? CoerceTo(string? colorKey, HoldMaterial? material) =>
        IsValidFor(colorKey, material) ? colorKey : null;

    public static HoldColor Get(string? colorKey) =>
        colorKey != null && ByKey.TryGetValue(colorKey, out var c) ? c : Unknown;

    public static string DisplayName(string? colorKey) =>
        string.IsNullOrEmpty(colorKey) ? Unknown.DisplayName : Get(colorKey).DisplayName;

    /// <summary>The solid hex a color renders as; the fallback pink for unknown colors.</summary>
    public static string Hex(string? colorKey) => Get(colorKey).Hex;

    /// <summary>The darker companion hex, used for polygon outlines.</summary>
    public static string StrokeHex(string? colorKey) => Get(colorKey).StrokeHex;

    /// <summary>
    /// The color as a translucent fill. Unknown colors fall back to a fainter pink so
    /// uncolored holds stay visually secondary.
    /// </summary>
    public static string Fill(string? colorKey, double alpha = 0.45)
    {
        var color = Get(colorKey);
        var effectiveAlpha = ReferenceEquals(color, Unknown) ? alpha * 0.78 : alpha;
        return Rgba(color.Hex, effectiveAlpha);
    }

    /// <summary>Converts a "#rrggbb" hex to an "rgba(r,g,b,a)" string.</summary>
    public static string Rgba(string hex, double alpha)
    {
        var (r, g, b) = ParseHex(hex);
        var a = alpha.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"rgba({r},{g},{b},{a})";
    }

    private static (int R, int G, int B) ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3)
        {
            h = string.Concat(h.Select(c => new string(c, 2)));
        }

        return (
            Convert.ToInt32(h[..2], 16),
            Convert.ToInt32(h.Substring(2, 2), 16),
            Convert.ToInt32(h.Substring(4, 2), 16));
    }
}
