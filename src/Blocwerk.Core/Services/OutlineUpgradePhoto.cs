namespace Blocwerk.Core.Services;

/// <summary>One live photo the outline upgrade walks: a panel row, or the legacy wall photo (no panel).</summary>
/// <param name="PanelId">The panel, or null for the legacy single-image photo.</param>
/// <param name="Generation">The generation its live holds carry.</param>
internal sealed record OutlineUpgradePhoto(Guid? PanelId, int Generation);
