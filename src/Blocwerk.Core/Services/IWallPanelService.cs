using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The big-wall multi-image topology: reading the live panel grid and its addable "+" frontier,
/// and the stage → confirm → discard lifecycle of adding a neighbouring panel (with cross-panel
/// hold re-recognition). Every wall is a big wall with a center (0,0) panel; the center panel is
/// seeded by the photo-upload path and the startup converge, not a per-wall toggle.
/// </summary>
public interface IWallPanelService
{
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
    /// Re-runs auto hold detection on a live panel's own image, replacing only the panel's
    /// auto-detected holds at the current generation that no boulder uses; manual and
    /// boulder-referenced holds are kept, so a redetect can never orphan a boulder. A panel with no
    /// live photo is a no-op. Returns the number of freshly detected holds.
    /// </summary>
    Task<int> RedetectPanelHoldsAsync(Guid wallId, Guid panelId);

    /// <summary>
    /// The live photo bytes of a panel, or null when the panel has none / is not on this wall.
    /// </summary>
    Task<WallPhoto?> GetPanelPhotoAsync(Guid wallId, Guid panelId);

    /// <summary>
    /// The staged photo bytes of a panel, or null when the panel has none / is not on this wall.
    /// </summary>
    Task<WallPhoto?> GetPanelStagedPhotoAsync(Guid wallId, Guid panelId);

    /// <summary>
    /// The live panel photo's <see cref="WallPhotoTag"/>, read without touching the blob, so a
    /// conditional request can be answered with 304 before any bytes leave Postgres.
    /// </summary>
    Task<WallPhotoTag?> GetPanelPhotoTagAsync(Guid wallId, Guid panelId);

    /// <summary>The same for the staged panel photo.</summary>
    Task<WallPhotoTag?> GetPanelStagedPhotoTagAsync(Guid wallId, Guid panelId);

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

    /// <summary>
    /// Adds a user-placed hold to the in-flight big-update staged set, in the panel image's
    /// normalized coordinate space. Structurally scoped to the staged generation (N+1): the hold is
    /// created at generation N+1 on the given staged panel, so this can never add a row to the live
    /// current generation. Throws when there is no in-flight update or <paramref name="panelId"/> is
    /// not a staged panel of it (any staged panel — centre or neighbour — is accepted, so the
    /// touch-up step can add missed holds on every panel). Returns the new hold's id. Gated by
    /// <see cref="WallAdminGuard.EnsureWallEditorAsync"/>.
    /// </summary>
    /// <param name="wallId">The wall that has the in-flight big update.</param>
    /// <param name="panelId">The staged panel (centre or neighbour) to add the hold to.</param>
    /// <param name="x">Normalized X (0-1) in the panel image's coordinate space.</param>
    /// <param name="y">Normalized Y (0-1) in the panel image's coordinate space.</param>
    /// <param name="radius">Normalized hold radius in the panel image's coordinate space.</param>
    /// <param name="color">Optional colour label or hex for the hold; null leaves it unset.</param>
    /// <param name="category">Optional hold category; null leaves it unset.</param>
    /// <param name="shapePoints">Optional custom outline points (centre-relative offsets); null for a circular hold.</param>
    /// <param name="material">Optional hold material; null leaves it unset.</param>
    /// <param name="handType">Optional hand-type classification; null leaves it unset.</param>
    /// <param name="needsReview">
    /// Whether the added hold is flagged as needing review. Defaults to <c>true</c> for the review
    /// pane's ad-hoc additions; the manual touch-up step passes <c>false</c> because those additions
    /// are corrections of the model's detection, not physical changes, and must not flag anything.
    /// </param>
    Task<Guid> AddStagedHoldAsync(
        Guid wallId,
        Guid panelId,
        double x,
        double y,
        double radius,
        string? color = null,
        HoldCategory? category = null,
        List<ShapePoint>? shapePoints = null,
        HoldMaterial? material = null,
        HoldHandType? handType = null,
        bool needsReview = true);

    /// <summary>
    /// Moves and resizes an existing staged hold. The hold is looked up filtered to the staged
    /// generation (N+1) of the in-flight update, so a live current-generation hold can never be
    /// matched and is therefore structurally impossible to mutate; a non-staged id throws. Touches
    /// only X/Y/Radius — it never sets any review/changed flag, so a touch-up reposition is a pure
    /// correction. Gated by <see cref="WallAdminGuard.EnsureWallEditorAsync"/>.
    /// </summary>
    Task UpdateStagedHoldAsync(Guid wallId, Guid holdId, double x, double y, double radius);

    /// <summary>
    /// Deletes a staged hold. Scoped identically to <see cref="UpdateStagedHoldAsync"/> — only a
    /// staged-generation hold of the in-flight update can be matched, so a live hold can never be
    /// deleted. Staged holds carry no boulders, so no boulder rescue is needed. Gated by
    /// <see cref="WallAdminGuard.EnsureWallEditorAsync"/>.
    /// </summary>
    Task DeleteStagedHoldAsync(Guid wallId, Guid holdId);

    /// <summary>
    /// Records that two holds on different panels of the same wall are the one physical hold seen
    /// across an overlapping seam — the standalone counterpart to the links created during the
    /// add-panel confirmation flow. Validates that both holds exist on <paramref name="wallId"/> and
    /// sit on two different panels, rejects a self-link, and dedupes on the unordered pair so
    /// re-linking is a no-op. Always creates a <see cref="HoldLinkKind.Same"/> link. Gated by
    /// <see cref="WallAdminGuard.EnsureWallEditorAsync"/>.
    /// </summary>
    Task CreateHoldLinkAsync(Guid wallId, Guid holdAId, Guid holdBId);

    /// <summary>
    /// Breaks the link between two holds on <paramref name="wallId"/>, matching the unordered pair.
    /// Idempotent: a no-op when no such link exists. Gated by
    /// <see cref="WallAdminGuard.EnsureWallEditorAsync"/>.
    /// </summary>
    Task DeleteHoldLinkAsync(Guid wallId, Guid holdAId, Guid holdBId);
}
