namespace Blocwerk.Core.Entities;

/// <summary>Lifecycle of a <see cref="WallCapture"/>. Stored as its integer value; never renumber.</summary>
public enum WallCaptureStatus
{
    /// <summary>Photos are being uploaded; nothing runs yet. Abandoned drafts are swept after a day.</summary>
    Draft = 0,

    /// <summary>Submitted; waiting for the (single) capture worker.</summary>
    Queued = 1,

    /// <summary>Reading photos and detecting markers.</summary>
    Detecting = 2,

    /// <summary>The compute service is solving the 3D wall model.</summary>
    Solving = 3,

    /// <summary>The model is active; the compute service is rendering per-facet textures.</summary>
    Texturing = 4,

    /// <summary>Done: model active and textures stored.</summary>
    Succeeded = 5,

    /// <summary>The model is active, but the textures could not be produced (see Error).</summary>
    SucceededWithoutTextures = 6,

    /// <summary>Stopped with an error (see Error). Nothing was activated.</summary>
    Failed = 7,

    /// <summary>
    /// Model and textures are done and live; the splat worker is training the photo-real view. Only
    /// reached when a splat service is configured.
    /// </summary>
    Splatting = 8,

    /// <summary>
    /// Model (and textures) are active, but the photo-real view could not be made (see Error). The
    /// capture itself succeeded; a texture failure still reports as <see cref="SucceededWithoutTextures"/>.
    /// </summary>
    SucceededWithoutSplat = 9,

    /// <summary>
    /// Done without an error: the model was computed and stored, but NOT activated, because it could not be tied to the
    /// active model's frame (see Error for the reason). Nothing downstream ran; an admin may activate it by hand.
    /// </summary>
    StoredNotActivated = 10,
}
