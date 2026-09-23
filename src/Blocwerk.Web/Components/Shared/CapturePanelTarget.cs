using System.Globalization;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Where a capture photo is aimed in <see cref="WallCapturePanelPicker"/>: a new panel on a free "+"
/// cell, or a cell of the full wall update. <see cref="Key"/> is the select option value.
/// </summary>
internal sealed record CapturePanelTarget(bool IsNewPanel, int Col, int Row)
{
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{(IsNewPanel ? 'n' : 'u')}:{Col}:{Row}");

    public static CapturePanelTarget NewPanel(int col, int row) => new(true, col, row);

    public static CapturePanelTarget Update(int col, int row) => new(false, col, row);

    /// <summary>The target behind an option value, or null for "not used" / anything malformed.</summary>
    public static CapturePanelTarget? Parse(string? key)
    {
        var parts = key?.Split(':');
        if (parts is not { Length: 3 } || parts[0] is not ("n" or "u")
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var col)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row))
        {
            return null;
        }

        return new CapturePanelTarget(parts[0] == "n", col, row);
    }
}
