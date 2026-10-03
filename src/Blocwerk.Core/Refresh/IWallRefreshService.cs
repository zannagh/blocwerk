// <copyright file="IWallRefreshService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Refresh;

/// <summary>The photo a user chose for one panel on the sort screen (null keeps the current photo).</summary>
public sealed record PanelChoice(int Col, int Row, Guid? PhotoId);

/// <summary>
/// "Update panels + 3D": one drop of a visit's photos and videos per wall; the photos are sorted to the panels,
/// the 3D capture and the panel update run from there, and nothing goes live before the user confirms.
/// Wall admins only, never from a kiosk tablet.
/// </summary>
public interface IWallRefreshService
{
    /// <summary>The wall's current run (open, or finished within the last day), or null.</summary>
    Task<WallRefreshView?> GetCurrentAsync(Guid wallId);

    /// <summary>
    /// The wall's current run like <see cref="GetCurrentAsync"/>, but without side effects: it neither queues the check
    /// against this visit's 3D model nor records a run whose photos were swept as discarded. For polling scripts.
    /// </summary>
    Task<WallRefreshView?> PeekCurrentAsync(Guid wallId);

    /// <summary>Queues the check against this visit's new 3D model when it is due; true when it is pending or running.</summary>
    Task<bool> RecheckAsync(Guid refreshId);

    /// <summary>The wall a run belongs to, or null when there is no such run. Reveals nothing else.</summary>
    Task<Guid?> GetWallIdAsync(Guid refreshId);

    /// <summary>Opens a run for the wall (or returns the open one).</summary>
    Task<WallRefreshView> BeginAsync(Guid wallId);

    /// <summary>Adds one photo (HEIC is fine). Refusals throw a <see cref="Services.UserFacingException"/>.</summary>
    Task<CapturePhotoResult> AddPhotoAsync(Guid refreshId, string? fileName, byte[] bytes, CancellationToken ct);

    /// <summary>Checks, before a file is read, that the caller may add it (wall admin, still uploading, room for a video).</summary>
    Task EnsureCanUploadAsync(Guid refreshId, bool isVideo);

    /// <summary>Stores one video for the 3D capture. Refused when the photo-real view is not set up here.</summary>
    Task<RefreshVideo> AddVideoAsync(Guid refreshId, string? fileName, Stream content, CancellationToken ct);

    /// <summary>Done uploading: the photos are sorted to the panels in the background.</summary>
    Task SortAsync(Guid refreshId);

    /// <summary>Starts the 3D capture and prepares the panel update with the chosen photos.</summary>
    /// <param name="refreshId">The run.</param>
    /// <param name="choices">The photo per panel.</param>
    /// <param name="keepModel">
    /// Panels only: no 3D capture is started (so no GPU runner is involved and the wall's active 3D model stays
    /// exactly as it is); the confirmed holds are still placed on that model's textures in the background.
    /// </param>
    Task StartAsync(Guid refreshId, IReadOnlyList<PanelChoice> choices, bool keepModel = false);

    /// <summary>The user's confirm: applies the prepared panel update, then places the holds on the 3D model.</summary>
    /// <param name="refreshId">The run.</param>
    /// <param name="confirmedVersion">
    /// The <see cref="RefreshSummary.DecisionsVersion"/> of the summary the user saw; refused when the summary changed since.
    /// </param>
    Task ApplyAsync(Guid refreshId, string? confirmedVersion = null);

    /// <summary>Throws the run away (and its prepared panel update); a started 3D capture keeps running.</summary>
    Task DiscardAsync(Guid refreshId);
}
