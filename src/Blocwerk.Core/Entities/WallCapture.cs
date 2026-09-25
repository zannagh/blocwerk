using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One in-app photo capture of a glyph wall: the admin uploads photos, the server detects markers,
/// has the 3D model solved by the geometry compute service, activates it and fetches per-facet
/// textures. Experimental and additive; photos live on disk (see <see cref="WallCapturePhoto"/>).
/// </summary>
public class WallCapture
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The admin who started it. The pipeline acts as this user (wall-admin check included).</summary>
    public Guid CreatedByUserId { get; set; }

    public WallCaptureStatus Status { get; set; } = WallCaptureStatus.Draft;

    /// <summary>Overall progress 0..1.</summary>
    public double Progress { get; set; }

    /// <summary>Short label of what is happening now, for the status panel.</summary>
    [MaxLength(200)]
    public string? Stage { get; set; }

    /// <summary>Why the capture failed (or why textures are missing). Safe to show an admin.</summary>
    [MaxLength(2048)]
    public string? Error { get; set; }

    /// <summary>The per-segment declarations and level pairs (<c>CaptureDeclarations</c> as JSON).</summary>
    public string? DeclarationsJson { get; set; }

    /// <summary>
    /// The marker plan this capture runs with (<c>MarkerPlanJson</c>): the plan uploaded with the photos,
    /// else a snapshot of the wall's plan taken at start. Null = the legacy <c>segment*6+role</c> convention.
    /// </summary>
    public string? PlanJson { get; set; }

    /// <summary>
    /// The wall's marker plan revision <see cref="PlanJson"/> is (<see cref="WallMarkerPlan.Revision"/>);
    /// null for the legacy convention.
    /// </summary>
    public int? PlanRevision { get; set; }

    /// <summary>The planned-vs-observed marker check made after the solve (<c>PlacementCheck</c> as JSON).</summary>
    public string? PlacementCheckJson { get; set; }

    [MaxLength(128)]
    public string? SolveJobId { get; set; }

    [MaxLength(128)]
    public string? TexturesJobId { get; set; }

    /// <summary>The photo-real (Gaussian splat) job, when a splat service is configured.</summary>
    [MaxLength(128)]
    public string? SplatJobId { get; set; }

    /// <summary>
    /// The quality profile of the photo-real view: chosen at start, changed by a retrain. Null = not
    /// recorded (captures from before the profiles, trained with the worker's settings of the day).
    /// </summary>
    public SplatQuality? SplatQuality { get; set; }

    /// <summary>
    /// The optional walk-along video (a bare name in the capture store) whose frames feed ONLY the
    /// photo-real stage. Deleted once its frames are extracted; null when there is none (any more).
    /// </summary>
    [MaxLength(128)]
    public string? VideoStoredPath { get; set; }

    /// <summary>The video's file name as uploaded, for the draft list.</summary>
    [MaxLength(256)]
    public string? VideoFileName { get; set; }

    public long? VideoSizeBytes { get; set; }

    public double? VideoDurationSeconds { get; set; }

    /// <summary>
    /// The stored names of the frames taken from the video (a JSON string array, in video order), once
    /// extracted. They are no <see cref="WallCapturePhoto"/>: never detected, solved or offered as panels.
    /// </summary>
    public string? VideoFramesJson { get; set; }

    /// <summary>The model this capture produced and activated, once it did.</summary>
    public Guid? GeometryModelId { get; set; }

    /// <summary>
    /// What the post-capture chain did with the new model (<c>CaptureFollowUpRecord</c> as JSON): one entry per
    /// step (placing the existing holds, refining their shapes, measuring them in the photo-real view), written
    /// after every step so a restarted app resumes the chain instead of redoing it. Null until the chain ran.
    /// </summary>
    public string? FollowUpJson { get; set; }

    /// <summary>
    /// What the capture saw of the wall and what the next capture should add (<c>CaptureCoverageReport</c> as JSON),
    /// written by the post-capture chain once the capture is done. Null until then.
    /// </summary>
    public string? CoverageJson { get; set; }

    [MaxLength(2048)]
    public string? Notes { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>How often the worker picked this capture up (restarts included); caps resume loops.</summary>
    public int Attempts { get; set; }
}
