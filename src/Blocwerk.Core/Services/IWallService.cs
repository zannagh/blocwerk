using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

public interface IWallService
{
    Task<Wall> CreateWallAsync(string name, string? description, int angle = 0);

    Task<Wall?> GetWallAsync(Guid wallId);

    Task<Wall?> GetWallByShareTokenAsync(string shareToken);

    /// <summary>
    /// The display names of every boulder's setters on the wall, keyed by boulder id, in one query.
    /// Boulders without a recorded setter are absent; callers fall back to the creator.
    /// </summary>
    Task<Dictionary<Guid, List<string>>> GetBoulderSetterNamesAsync(Guid wallId);

    Task<List<Wall>> GetMyWallsAsync();

    Task<Wall> UpdateWallAsync(Guid wallId, string name, string? description, int? angle = null);

    Task DeleteWallAsync(Guid wallId);

    Task<Wall> UploadPhotoAsync(Guid wallId, byte[] photo, string contentType, bool autoDetect = true);

    Task<Wall> StagePhotoAsync(Guid wallId, byte[] photo, string contentType);

    Task<Wall> StageManualAlignmentAsync(Guid wallId, byte[] photo, string contentType);

    /// <summary>
    /// Stages a full wall recreation: new photo plus fresh detection at the staged
    /// generation. Unlike the other staging modes the live holds are neither cloned
    /// nor aligned - on confirm they stay behind at the current generation.
    /// </summary>
    Task<Wall> StageRecreateAsync(Guid wallId, byte[] photo, string contentType);

    Task<Wall> ConfirmStagedPhotoAsync(Guid wallId);

    /// <summary>
    /// Promotes a staged recreation: bumps the generation, leaves the previous holds
    /// behind for historic boulders, archives the retired photo and marks every live
    /// boulder historic. Ascents, comments and grade proposals are untouched.
    /// </summary>
    Task<WallRecreateResult> ConfirmRecreateAsync(Guid wallId);

    Task<Wall> ConfirmManualAlignmentAsync(Guid wallId, List<ManualAlignHold> holds, List<Guid> deletedStagedIds);

    /// <summary>
    /// Estimates the old-photo -> staged-photo transform locally (normalized 0-1
    /// coordinates). Returns null when no reliable alignment could be found.
    /// Callers apply it to the overlay holds in-memory so it flows through the
    /// editor's normal Save/Discard.
    /// </summary>
    Task<Homography?> EstimateStagingAlignmentAsync(Guid wallId);

    Task DiscardStagedPhotoAsync(Guid wallId);

    Task<byte[]?> GetStagedPhotoAsync(Guid wallId);

    /// <summary>
    /// The staged photo's <see cref="WallPhotoTag"/>, read without touching the blob, so a
    /// conditional request can be answered with 304 before any bytes leave Postgres.
    /// </summary>
    Task<WallPhotoTag?> GetStagedPhotoTagAsync(Guid wallId);

    Task<Hold> MarkHoldModifiedAsync(Guid holdId);

    /// <summary>
    /// Recovers boulders that went historic during a wall update whose holds are all fine now.
    /// For each historic boulder referencing <paramref name="holdId"/>, it un-historics the boulder
    /// only if every hold it references still exists (no removed/dangling hold); boulders that lost a
    /// hold are left historic. Deliberately conservative — the sole restore rule is "all its holds
    /// still exist". Returns the number of boulders restored.
    /// </summary>
    Task<int> RestoreBouldersForUnchangedHoldAsync(Guid holdId, CancellationToken ct = default);

    Task<Hold> MergeHoldsAsync(Guid stagedHoldId, Guid liveHoldId);

    /// <summary>
    /// Makes a virtual (placeholder) hold actual by merging it into an existing detected hold.
    /// The virtual hold survives (keeping its Id so its boulders stay linked) and adopts the
    /// detected hold's geometry and appearance; the detected hold's own boulder links are
    /// re-pointed onto the survivor and it is then deleted. Not gated to staging.
    /// </summary>
    Task MergeVirtualHoldAsync(Guid virtualHoldId, Guid actualHoldId, CancellationToken ct = default);

    /// <summary>
    /// Promotes a virtual (placeholder) hold to an actual hold in place, clearing its virtual
    /// flag while leaving its Id, geometry and boulder links untouched. Not gated to staging.
    /// </summary>
    Task PromoteVirtualHoldAsync(Guid virtualHoldId, CancellationToken ct = default);

    Task<string> GenerateShareTokenAsync(Guid wallId);

    /// <summary>
    /// Returns the wall's share token so any member can invite others, minting one only when the
    /// wall has none yet. Non-destructive: an existing token is returned unchanged (unlike
    /// <see cref="GenerateShareTokenAsync"/>, which regenerates). Refused for kiosk sessions.
    /// </summary>
    Task<string> GetOrCreateShareTokenAsync(Guid wallId);

    Task<Wall> JoinWallAsync(string shareToken);

    Task<byte[]?> GetPhotoAsync(Guid wallId);

    Task<byte[]?> GetPhotoByShareTokenAsync(Guid wallId, string shareToken);

    /// <summary>
    /// The live photo's <see cref="WallPhotoTag"/> under the same gate as
    /// <see cref="GetPhotoAsync"/> (or <see cref="GetPhotoByShareTokenAsync"/> when
    /// <paramref name="shareToken"/> is given), read without touching the blob.
    /// </summary>
    Task<WallPhotoTag?> GetPhotoTagAsync(Guid wallId, string? shareToken);

    /// <summary>
    /// The holds captured at a specific, possibly retired, generation. Used to render
    /// a historic boulder against the wall as it looked when it was set.
    /// </summary>
    Task<List<Hold>> GetHoldsForGenerationAsync(Guid wallId, int generation);

    /// <summary>
    /// The wall photo as it looked at the given generation. Falls back to the live
    /// photo for the current generation; null when that generation was never archived.
    /// </summary>
    Task<WallPhoto?> GetPhotoForGenerationAsync(Guid wallId, int generation);

    Task<WallPhoto?> GetPhotoForGenerationByShareTokenAsync(Guid wallId, string shareToken, int generation);

    /// <summary>
    /// The <see cref="WallPhotoTag"/> of the photo <see cref="GetPhotoForGenerationAsync"/> would
    /// return, read without touching the blob. A retired generation comes back with
    /// <see cref="WallPhotoTag.IsArchived"/> set, because those bytes can never change again.
    /// </summary>
    Task<WallPhotoTag?> GetPhotoTagForGenerationAsync(Guid wallId, string? shareToken, int generation);

    Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color, HoldCategory category = HoldCategory.Hand, List<ShapePoint>? shapePoints = null, bool isVirtual = false, HoldMaterial? material = null, HoldHandType? handType = null);

    Task<Hold> UpdateHoldAsync(Guid holdId, double x, double y, double radius, string? color = null, HoldCategory? category = null, bool? isOnKickboard = null, List<ShapePoint>? shapePoints = null, string? name = null, HoldMaterial? material = null, bool flagBouldersOnMove = true, HoldHandType? handType = null);

    Task DeleteHoldAsync(Guid holdId);

    Task<int> RedetectHoldsAsync(Guid wallId, HoldDetectionParameters? parameters = null);

    Task ClearAutoDetectedHoldsAsync(Guid wallId);

    Task SetBorderPointsAsync(Guid wallId, List<ShapePoint> points);

    Task<int> CleanOutsideBorderAsync(Guid wallId);

    Task<List<WallMember>> GetMembersAsync(Guid wallId);

    /// <summary>
    /// True when both users are members of at least one common wall. Used to gate whether a viewer
    /// may see another member's profile. Independent of the current-user context.
    /// </summary>
    Task<bool> UsersShareAWallAsync(Guid userA, Guid userB);

    Task SetMemberRoleAsync(Guid wallId, Guid userId, WallRole role);

    /// <summary>
    /// Puts the wall into (or out of) "update mode". While enabled, every viewer except the admin who
    /// enabled it sees a "currently being updated" notice instead of the wall. Requires the caller to
    /// be the wall owner or an Admin member.
    /// </summary>
    Task SetMaintenanceAsync(Guid wallId, bool underMaintenance);

    /// <summary>
    /// Turns anonymous kiosk setting on or off for this wall (<c>Wall.AllowAnonymousKioskSetting</c>).
    /// Requires the caller to be the wall owner or an Admin member.
    /// </summary>
    /// <remarks>
    /// Off by default and off for every existing wall: this is the opt-in behind the app's only
    /// unauthenticated write, so it must be a decision somebody made rather than one they inherited.
    /// A kiosk session cannot call it — the wall is administered from an admin's own device — which
    /// keeps a tablet from granting itself the capability.
    /// </remarks>
    Task SetAnonymousKioskSettingAsync(Guid wallId, bool allowed);
}
