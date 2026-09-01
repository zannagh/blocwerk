using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Holds;

/// <summary>
/// Builds the human-readable "info bubble" lines shared by every read-only hold tap surface
/// (the boulder detail overlay and the multi-image wall viewer). Only the set fields are
/// emitted, in a fixed order: Colour, Material, Sub-type, Name. Sub-type is only meaningful
/// for hand holds.
/// </summary>
public static class HoldInfoText
{
    /// <summary>
    /// The set fields to show in a hold's info bubble, skipping any that are unset. Kept as one
    /// helper so the single-image (boulder detail) and multi-image (wall) bubbles never drift.
    /// </summary>
    public static IReadOnlyList<string> Lines(
        string? color,
        HoldMaterial? material,
        HoldCategory category,
        HoldHandType? handType,
        string? name)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(color))
        {
            lines.Add($"Color: {HoldPalette.DisplayName(color)}");
        }

        if (material.HasValue)
        {
            lines.Add($"Material: {(material.Value == HoldMaterial.DualTex ? "DualTex" : material.Value.ToString())}");
        }

        if (category == HoldCategory.Hand && handType.HasValue)
        {
            lines.Add($"Sub-type: {handType.Value}");
        }

        if (!string.IsNullOrEmpty(name))
        {
            lines.Add($"Name: {name}");
        }

        return lines;
    }
}
