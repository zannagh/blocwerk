namespace Blocwerk.Web.State;

/// <summary>
/// The kind of unsaved, in-flight editing work that makes the app "busy" for the deploy gate.
/// </summary>
public enum EditKind
{
    /// <summary>A user has the boulder-create page open (a live create session).</summary>
    BoulderCreate,

    /// <summary>A user has the boulder-revise page open (unsaved hold/rule edits).</summary>
    BoulderRevise,

    /// <summary>A user has an inline boulder editor open (name/grade, grade proposal, backdate).</summary>
    BoulderEdit,

    /// <summary>A user has the wall-create page open (an unsaved new-wall draft).</summary>
    WallCreate,

    /// <summary>A user has a wall in edit/alignment mode.</summary>
    WallEdit,

    /// <summary>
    /// An admin-triggered maintenance job is running (cache warming, avatar normalisation). Not an
    /// unsaved edit, but it holds the same lease for the same reason: recreating the container
    /// underneath it would abort the run.
    /// </summary>
    Maintenance,

    /// <summary>
    /// A capture's walk-along video is streaming in over HTTP. Background work like
    /// <see cref="Maintenance"/>: an upload has no circuit to heartbeat it and can take well over the
    /// inactivity TTL, and a restart loses it outright.
    /// </summary>
    CaptureVideoUpload,

    /// <summary>The capture worker is extracting frames from a walk-along video (ffmpeg).</summary>
    CaptureVideoFrames,
}
