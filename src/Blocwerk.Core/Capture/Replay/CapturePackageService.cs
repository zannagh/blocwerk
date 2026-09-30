// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Data;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// <see cref="ICapturePackageService"/>. Every call is for app administrators (<see cref="AppAdminGuard"/>, decided
/// against the database like the administration area); the root context is used because an administrator need not be
/// a member of the wall.
/// </summary>
/// <remarks>
/// The import keeps the source's ids and stored names, so the JSON inside the rows (photo names in the model, texture
/// source maps, the prepared state) stays valid. It inserts only what the target cannot recompute without training:
/// capture, photos, model, textures, and the trained view as a GPU job in the "delivered by a runner, not installed"
/// state (<see cref="WallCaptureProcessor"/>'s resume path finishes it). Photo-real views, volumes, proposals, placement
/// runs and hold changes are recomputed by the target's pipeline.
/// </remarks>
public sealed partial class CapturePackageService(
    RootDbContextFactory dbContextFactory,
    ICurrentUserService currentUser,
    ICaptureFileStore files,
    WallCaptureQueue queue,
    IComputeJobClientFactory computeClients,
    ILogger<CapturePackageService> logger,
    GpuRunnerOptions? runnerOptions = null,
    DiskSpaceProbe? diskSpace = null,
    WallCapturePipelineOptions? pipelineOptions = null) : ICapturePackageService
{
    private readonly CapturePackageStaging staging = new(files);
    private readonly DiskSpaceProbe disk = diskSpace ?? new DiskSpaceProbe();

    public async Task AbortImportAsync(Guid importId, CancellationToken ct)
    {
        var userId = await EnsureAdminAsync(ct);
        staging.Delete(importId);
        logger.LogInformation("Capture import {ImportId} abandoned by {UserId}", importId, userId);
    }

    /// <summary>The acting user's id when they administer the whole installation; else <see cref="UnauthorizedAccessException"/>.</summary>
    private async Task<Guid> EnsureAdminAsync(CancellationToken ct)
    {
        var user = await currentUser.GetCurrentUserAsync()
                   ?? throw new UnauthorizedAccessException("Sign in first.");
        await using var db = dbContextFactory.CreateDbContext();
        await AppAdminGuard.EnsureAppAdminAsync(db, user.Id, ct);
        return user.Id;
    }
}
