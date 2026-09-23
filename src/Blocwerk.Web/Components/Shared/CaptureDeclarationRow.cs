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

    public CaptureSegmentDeclaration ToDeclaration() => new(
        Index,
        string.IsNullOrWhiteSpace(Name) ? $"Segment {Index}" : Name.Trim(),
        AngleDeg,
        Vertical);
}
