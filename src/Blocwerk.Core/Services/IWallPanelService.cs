using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The big-wall multi-image topology: enabling/disabling the feature per wall, reading the
/// live panel grid and its addable "+" frontier, and the stage → confirm → discard lifecycle
/// of adding a neighbouring panel (with cross-panel hold re-recognition).
/// </summary>
public interface IWallPanelService
{
    /// <summary>
    /// Turns on multi-image mode. Idempotent: a no-op when already enabled. On first enable it
    /// creates the center panel (0,0) mirroring <c>Wall.Photo</c> and re-parents every
    /// current-generation, unassigned hold onto that center panel.
    /// </summary>
    Task EnableMultiImageAsync(Guid wallId);

    /// <summary>
    /// Turns off multi-image mode without destroying anything (panels, holds and links are kept).
    /// </summary>
    Task DisableMultiImageAsync(Guid wallId);

    /// <summary>
    /// The wall's panels, placement only — no photo bytes. Includes both live panels (promoted
    /// photo) and staged-only panels still mid-confirmation; distinguish via
    /// <see cref="WallPanelInfo.IsLive"/>.
    /// </summary>
    Task<IReadOnlyList<WallPanelInfo>> GetPanelsAsync(Guid wallId);

    /// <summary>
    /// The empty grid cells orthogonally adjacent to at least one live panel — the "+" slots.
    /// </summary>
    Task<IReadOnlyList<PanelPosition>> GetFrontierPositionsAsync(Guid wallId);

    /// <summary>
    /// Stages a new panel at (col,row): validates the slot is empty and adjacent to a live panel,
    /// runs hold detection on the image, persists the detected holds against the new panel, then
    /// matches them against every adjacent live neighbour and returns the overlap proposals.
    /// </summary>
    Task<StagePanelResult> StagePanelAsync(Guid wallId, int col, int row, byte[] image, string contentType);

    /// <summary>
    /// Re-opens a stranded staged panel: the panel and its detected holds already live in the DB,
    /// but its overlap proposals were only ever held in memory. Regenerates them on demand from the
    /// panel's staged photo and holds so the confirmation flow can be resumed. Requires the panel to
    /// be staged (a staged photo, not yet promoted); returns the same shape as
    /// <see cref="StagePanelAsync"/>.
    /// </summary>
    Task<StagePanelResult> ResumePanelAsync(Guid wallId, Guid panelId);

    /// <summary>
    /// Promotes a staged panel to live, persisting the user-confirmed hold links first and then
    /// deleting the neighbour holds the user marked as physically removed from the wall. Links and
    /// removals are applied atomically in one save; discarding the panel applies neither.
    /// </summary>
    Task ConfirmPanelAsync(
        Guid wallId,
        Guid panelId,
        IReadOnlyList<ConfirmedLink> links,
        IReadOnlyList<Guid> removedNeighborHoldIds);

    /// <summary>
    /// Discards a staged panel: deletes its detected holds first, then the panel itself.
    /// </summary>
    Task DiscardPanelAsync(Guid wallId, Guid panelId);

    /// <summary>
    /// The live photo bytes of a panel, or null when the panel has none / is not on this wall.
    /// </summary>
    Task<WallPhoto?> GetPanelPhotoAsync(Guid wallId, Guid panelId);

    /// <summary>
    /// The staged photo bytes of a panel, or null when the panel has none / is not on this wall.
    /// </summary>
    Task<WallPhoto?> GetPanelStagedPhotoAsync(Guid wallId, Guid panelId);

    /// <summary>
    /// The panel's holds at the wall's current generation, in the panel image's normalized
    /// coordinate space — used to draw the overlap confirmation overlay. When
    /// <paramref name="includeStaged"/> is false a panel that only carries a staged (not yet
    /// promoted) photo returns nothing; a live panel returns its holds regardless. Empty when the
    /// wall is not visible to the caller or the panel is not on this wall.
    /// </summary>
    Task<IReadOnlyList<PanelHold>> GetPanelHoldsAsync(Guid wallId, Guid panelId, bool includeStaged);

    /// <summary>
    /// The panel's full live-generation <see cref="Entities.Hold"/> entities — the editable working
    /// set for per-panel hold editing on a big wall. Mirrors the visibility gating of
    /// <see cref="GetPanelHoldsAsync"/> (live generation, no staged rows) but returns whole entities
    /// rather than the thin <see cref="PanelHold"/> projection. Empty when the wall or panel is not
    /// visible to the caller.
    /// </summary>
    Task<IReadOnlyList<Hold>> GetPanelHoldEntitiesAsync(Guid wallId, Guid panelId);

    /// <summary>
    /// The wall's hold links — pairs of holds recorded as the same physical hold across two
    /// overlapping panels. Visibility-gated the same way the other reads are: a wall the caller
    /// cannot see yields nothing. Both link kinds (Same and Moved) are returned; the caller treats
    /// each pair as "the same physical hold".
    /// </summary>
    Task<IReadOnlyList<HoldLinkPair>> GetHoldLinksAsync(Guid wallId);

    /// <summary>
    /// Adds a user-placed hold to a panel during overlap confirmation, for when the matcher
    /// missed a hold the user needs to link. Creates a non-auto-detected hold at the current
    /// wall generation, flagged for review, in the panel image's normalized coordinate space,
    /// and returns its id. Gated by <see cref="WallAdminGuard"/>.
    /// </summary>
    Task<Guid> AddPanelHoldAsync(
        Guid wallId,
        Guid panelId,
        double x,
        double y,
        double radius,
        string? color = null,
        HoldCategory? category = null,
        List<ShapePoint>? shapePoints = null,
        HoldMaterial? material = null,
        HoldHandType? handType = null);
}
