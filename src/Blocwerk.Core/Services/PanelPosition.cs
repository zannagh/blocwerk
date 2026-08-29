namespace Blocwerk.Core.Services;

/// <summary>
/// An empty grid cell orthogonally adjacent to a live panel — a "+" slot where a new panel
/// may be added.
/// </summary>
/// <param name="Col">Grid column.</param>
/// <param name="Row">Grid row.</param>
public record PanelPosition(int Col, int Row);
