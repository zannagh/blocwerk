using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

public interface IWallService
{
    Task<Wall> CreateWallAsync(string name, string? description, int angle = 0);

    Task<Wall?> GetWallAsync(Guid wallId);

    Task<Wall?> GetWallByShareTokenAsync(string shareToken);

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

    Task<Hold> MarkHoldModifiedAsync(Guid holdId);

    Task<Hold> MergeHoldsAsync(Guid stagedHoldId, Guid liveHoldId);

    Task<string> GenerateShareTokenAsync(Guid wallId);

    Task<Wall> JoinWallAsync(string shareToken);

    Task<byte[]?> GetPhotoAsync(Guid wallId);

    Task<byte[]?> GetPhotoByShareTokenAsync(Guid wallId, string shareToken);

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

    Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color, HoldCategory category = HoldCategory.Hand, List<ShapePoint>? shapePoints = null, bool isVirtual = false, HoldMaterial? material = null);

    Task<Hold> UpdateHoldAsync(Guid holdId, double x, double y, double radius, string? color = null, HoldCategory? category = null, bool? isOnKickboard = null, List<ShapePoint>? shapePoints = null, string? name = null, HoldMaterial? material = null);

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
}
