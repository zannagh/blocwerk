// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>
/// Managing 3D runners from the UI: every call is a wall-admin (or runner-owner, or site-admin)
/// action and is refused from a kiosk tablet.
/// </summary>
public interface IGpuRunnerService
{
    /// <summary>Runners that can serve this wall: its own, plus other admins' shared runners.</summary>
    Task<IReadOnlyList<GpuRunnerInfo>> ListForWallAsync(Guid wallId);

    /// <summary>This wall's GPU jobs that are waiting, running, or delivered and not installed yet.</summary>
    Task<IReadOnlyList<GpuJobInfo>> ListJobsForWallAsync(Guid wallId);

    /// <summary>Creates a runner serving this wall and the caller's other walls; the key is in the result only.</summary>
    Task<GpuRunnerCreated> CreateAsync(Guid wallId, string name);

    /// <summary>Revokes a runner (owner or site admin); its key stops working at once and its job is requeued.</summary>
    Task RevokeAsync(Guid runnerId);

    /// <summary>"Other walls can use this runner".</summary>
    Task SetSharedAsync(Guid runnerId, bool shared);

    /// <summary>The walls the runner's owner administers, and which of them it serves.</summary>
    Task<IReadOnlyList<GpuRunnerWallChoice>> GetWallChoicesAsync(Guid runnerId);

    /// <summary>Adds or removes one of the owner's walls.</summary>
    Task SetServesWallAsync(Guid runnerId, Guid wallId, bool serves);

    /// <summary>Whether the wall lets other admins' shared runners train its photo-real view (wall admin).</summary>
    Task<bool> GetSharedOptInAsync(Guid wallId);

    /// <summary>Opts the wall in to (or out of) shared runners (wall admin). Its photos then go to their machines.</summary>
    Task SetSharedOptInAsync(Guid wallId, bool accept);

    /// <summary>Every runner on the server (site admin only).</summary>
    Task<IReadOnlyList<GpuRunnerInfo>> ListAllAsync();

    /// <summary>Cancels the waiting/running GPU job of a capture (wall admin); the capture keeps its model and any older view.</summary>
    Task<bool> CancelCaptureJobAsync(Guid captureId);
}
