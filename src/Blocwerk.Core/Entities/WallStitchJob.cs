using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One run of the external stitch sidecar: several handheld photos of a wall are registered,
/// rectified into an ortho/angled pair and (optionally) the wall's existing holds are transferred
/// onto the result. The row mirrors the sidecar's job state so the UI can poll our database
/// instead of the sidecar, and so an abandoned job is still visible after a restart.
/// </summary>
public class WallStitchJob
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The user who started the run. Must be an admin of <see cref="WallId"/>.</summary>
    public Guid RequestedByUserId { get; set; }

    /// <summary>Identifier the sidecar assigned to the job. Null until <c>POST /jobs</c> returns.</summary>
    [MaxLength(64)]
    public string? SidecarJobId { get; set; }

    public WallStitchJobStatus Status { get; set; } = WallStitchJobStatus.Queued;

    /// <summary>Sidecar-reported completion between 0.0 and 1.0.</summary>
    public double Progress { get; set; }

    /// <summary>Sidecar-reported phase name (registering, rectifying, blending, matching...).</summary>
    [MaxLength(64)]
    public string? Stage { get; set; }

    [MaxLength(64)]
    public string? ErrorCode { get; set; }

    [MaxLength(1024)]
    public string? ErrorMessage { get; set; }

    /// <summary>Which projection the caller wants to become the wall's default photo.</summary>
    public WallPhotoProjection RequestedProjection { get; set; } = WallPhotoProjection.Angled;

    /// <summary>Wall inclination used to build the angled projection, in degrees from vertical.</summary>
    public double WallAngleDegrees { get; set; }

    /// <summary>Whether the sidecar was asked to carry the wall's existing holds onto the result.</summary>
    public bool TransferHolds { get; set; }

    /// <summary>How many photos were submitted (2..12).</summary>
    public int PhotoCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Raw <c>diagnostics</c> object from the sidecar result (images used/rejected, seam angle RMS,
    /// bow median, coverage warnings), stored verbatim so the shape can evolve sidecar-side.
    /// </summary>
    public string? DiagnosticsJson { get; set; }
}
