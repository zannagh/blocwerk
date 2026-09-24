namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>One row of a wall export's <c>textures/textures.json</c>: a facet texture, its mask and its plane grid.</summary>
internal sealed record AtticExportedTexture(
    string FacetId, string File, string? MaskFile, double AMin, double AMax, double BMin, double BMax, int WidthPx, int HeightPx);
