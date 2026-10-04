// <copyright file="WallRefresh.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One "Update panels + 3D" run of a wall: the photos and videos of one visit, sorted to panels, driving the
/// 3D capture and the panel update from a single row that a background worker moves forward.
/// </summary>
public class WallRefresh
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public Guid CreatedByUserId { get; set; }

    public WallRefreshStatus Status { get; set; } = WallRefreshStatus.Uploading;

    /// <summary>The capture draft that holds the uploaded photos (and later runs the 3D capture).</summary>
    public Guid? CaptureId { get; set; }

    /// <summary>True once the capture was actually started (false when 3D is not set up or it refused).</summary>
    public bool CaptureStarted { get; set; }

    /// <summary>The big-update session the panel update runs in.</summary>
    public Guid? UpdateSessionId { get; set; }

    /// <summary>The uploaded videos (stored names, file names, sizes), as JSON.</summary>
    public string? VideosJson { get; set; }

    /// <summary>The proposed and then confirmed photo per panel, as JSON.</summary>
    public string? PanelPicksJson { get; set; }

    /// <summary>The timeline shown on the progress page, as JSON.</summary>
    public string? StepsJson { get; set; }

    /// <summary>What the confirm screen shows (carried, new, dropped, boulders affected), as JSON.</summary>
    public string? SummaryJson { get; set; }

    /// <summary>
    /// The decisions version the user confirmed when Apply was accepted (<see cref="Refresh.RefreshSummary.DecisionsVersion"/>
    /// of the summary on their screen). The background apply promotes only when the live decisions still have exactly this
    /// version; cleared when the run goes back to the confirm screen.
    /// </summary>
    [MaxLength(64)]
    public string? ConfirmedDecisionsVersion { get; set; }

    [MaxLength(2048)]
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
