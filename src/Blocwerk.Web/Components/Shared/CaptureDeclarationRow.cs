using Blocwerk.Core.Capture;

namespace Blocwerk.Web.Components.Shared;

/// <summary>An editable row of the capture declarations table (one marker segment).</summary>
public sealed class CaptureDeclarationRow
{
    public int Index { get; init; }

    public string Name { get; set; } = string.Empty;

    public double? AngleDeg { get; set; }

    public bool Vertical { get; set; }

    public static CaptureDeclarationRow From(CaptureSegmentDeclaration d) => new()
    {
        Index = d.Index,
        Name = d.Name,
        AngleDeg = d.DeclaredAngleDeg,
        Vertical = d.VerticalReference,
    };

    /// <summary>Shown under the row when its markers would be merged into another face (a named row without an angle).</summary>
    public string? MergeWarning => CaptureDeclarationRules.MergeWarningFor(ToDeclaration());

    public CaptureSegmentDeclaration ToDeclaration() => new(
        Index,
        string.IsNullOrWhiteSpace(Name) ? CaptureDeclarationRules.DefaultName(Index) : Name.Trim(),
        AngleDeg,
        Vertical);
}
