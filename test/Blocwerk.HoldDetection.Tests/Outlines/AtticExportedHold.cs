using System.Text.Json.Serialization;
using Blocwerk.Core.Entities;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>One row of a wall export's <c>holds.json</c>: the hold columns the outline upgrade reads, plus the panel position.</summary>
internal sealed class AtticExportedHold
{
    public Guid Id { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Radius { get; set; }

    public int Generation { get; set; }

    public Guid? WallPanelId { get; set; }

    public bool IsAutoDetected { get; set; }

    public bool IsVirtual { get; set; }

    public List<ShapePoint>? ShapePoints { get; set; }

    [JsonPropertyName("panelCol")]
    public int? PanelCol { get; set; }

    [JsonPropertyName("panelRow")]
    public int? PanelRow { get; set; }

    public Hold ToHold() => new()
    {
        Id = Id,
        X = X,
        Y = Y,
        Radius = Radius,
        Generation = Generation,
        WallPanelId = WallPanelId,
        IsAutoDetected = IsAutoDetected,
        IsVirtual = IsVirtual,
        ShapePoints = ShapePoints,
    };
}
