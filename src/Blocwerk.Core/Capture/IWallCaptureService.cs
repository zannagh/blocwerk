namespace Blocwerk.Core.Capture;

/// <summary>
/// The in-app glyph capture: an admin uploads photos of a marker wall, the server does the rest
/// (markers, 3D solve on the compute worker, activation, textures). Every write is gated like the
/// other wall-admin actions — owner or admin of the wall, never from a kiosk tablet — and capture
/// operations derive the wall from the capture row, never from a client-supplied wall id.
/// </summary>
public interface IWallCaptureService
{
    /// <summary>False when no geometry compute service is configured; the UI then offers only the JSON import.</summary>
    bool IsComputeConfigured { get; }

    /// <summary>
    /// True when a splat (photo-real) worker is configured and frames can be taken from a video: only
    /// then does the UI offer the optional walk-along video.
    /// </summary>
    bool IsSplatConfigured { get; }

    /// <summary>
    /// Streams the draft's optional walk-along video to disk (replacing an earlier one) and checks it
    /// is a readable video. Its frames later feed ONLY the photo-real stage: no markers, no solve, no
    /// panel photos, not counted against <see cref="WallCapturePipelineOptions.MaxPhotos"/>. Admin only,
    /// never from a kiosk, only for a glyph wall with a splat worker. Refusals throw
    /// <see cref="InvalidOperationException"/> with a message for the admin.
    /// </summary>
    Task<CaptureVideoInfo> AddVideoAsync(Guid captureId, string? fileName, Stream content, CancellationToken ct);

    /// <summary>Removes the draft's video (no-op without one).</summary>
    Task RemoveVideoAsync(Guid captureId);

    /// <summary>The current user's open draft for the wall (with its photos), or null. Admin only.</summary>
    Task<WallCaptureDraft?> GetDraftAsync(Guid wallId);

    /// <summary>Opens a draft (or returns the user's existing one). Refused unless the wall declares markers.</summary>
    Task<WallCaptureDraft> CreateDraftAsync(Guid wallId);

    /// <summary>
    /// Stores one photo (metadata stripped), reads its EXIF camera facts and detects its markers.
    /// Refusals (HEIC, not an image, too big, too many, duplicate) throw <see cref="InvalidOperationException"/>
    /// with a message for the admin.
    /// </summary>
    Task<CapturePhotoResult> AddPhotoAsync(Guid captureId, string? fileName, byte[] bytes, CancellationToken ct);

    Task RemovePhotoAsync(Guid captureId, Guid photoId);

    /// <summary>Deletes a draft and its files.</summary>
    Task DiscardDraftAsync(Guid captureId);

    /// <summary>
    /// Uses an uploaded marker plan JSON for this draft (null/empty removes it again). Refused with the
    /// parse/validation errors when it is not a usable plan. When it differs from the wall's current plan
    /// it is saved as the wall's next plan revision; the draft records the revision either way.
    /// </summary>
    Task<CapturePlanResult> AttachPlanAsync(Guid captureId, string? planJson);

    /// <summary>
    /// Runs the draft with a stored revision of the wall's plan (e.g. the photos were taken before the
    /// markers of the current revision were mounted). Refused with a reason when the revision does not exist.
    /// </summary>
    Task<CapturePlanResult> UsePlanRevisionAsync(Guid captureId, int revision);

    /// <summary>
    /// Declarations for the segments seen in the draft's photos: from the marker plan when the draft has
    /// one; else pre-filled from the wall's previous capture, else from segments bound to a marker index,
    /// else defaults.
    /// </summary>
    Task<CaptureDeclarations> SuggestDeclarationsAsync(Guid captureId);

    /// <summary>Submits the draft to the pipeline. Returns the problems that prevent it (empty = started).</summary>
    Task<IReadOnlyList<string>> StartAsync(Guid captureId, CaptureDeclarations declarations, string? notes);

    /// <summary>
    /// The photos of any capture (draft or finished), for reuse as panel photos. Admin only, never a
    /// kiosk; the wall comes from the capture row. Reading never changes the capture.
    /// </summary>
    Task<IReadOnlyList<CapturePhotoResult>> GetPhotosAsync(Guid captureId);

    /// <summary>
    /// One photo's stored (metadata-stripped) bytes, gated like <see cref="GetPhotosAsync"/>. Throws
    /// <see cref="InvalidOperationException"/> when the photo is not part of the capture or its file is gone.
    /// </summary>
    Task<CapturePhotoFile> ReadPhotoAsync(Guid captureId, Guid photoId, CancellationToken ct);

    /// <summary>The wall's captures, newest first, drafts excluded. Admin only.</summary>
    Task<IReadOnlyList<WallCaptureSummary>> GetCapturesAsync(Guid wallId);

    /// <summary>One capture, for polling. Admin only.</summary>
    Task<WallCaptureSummary?> GetCaptureAsync(Guid captureId);

    /// <summary>
    /// The textures of the wall's ACTIVE geometry model, for the 3D view. Visible to whoever may view
    /// the wall: a member, a share-link holder (pass the token; it is carried into the URLs) or the
    /// wall's own kiosk tablet. Empty when there is no model or it has no textures.
    /// </summary>
    Task<IReadOnlyList<WallGeometryTextureInfo>> GetActiveTexturesAsync(Guid wallId, string? shareToken = null);
}
