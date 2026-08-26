using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Stitching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <inheritdoc cref="IWallStitchService"/>
public class WallStitchService : IWallStitchService
{
    private const int MinPhotos = 2;
    private const int MaxPhotos = 12;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IWallStitchClient client;
    private readonly IWallPhotoMasterStorage masterStorage;
    private readonly ILogger<WallStitchService> logger;

    public WallStitchService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IWallStitchClient client,
        IWallPhotoMasterStorage masterStorage,
        ILogger<WallStitchService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.client = client;
        this.masterStorage = masterStorage;
        this.logger = logger;
    }

    public async Task<WallStitchJob> StartJobAsync(
        Guid wallId,
        Guid actingUserId,
        IReadOnlyList<StitchPhotoUpload> photos,
        WallStitchStartOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(options);

        if (photos.Count is < MinPhotos or > MaxPhotos)
        {
            throw new ArgumentException($"A stitch job needs {MinPhotos}..{MaxPhotos} photos.", nameof(photos));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, actingUserId, ct);

        var job = new WallStitchJob
        {
            WallId = wallId,
            RequestedByUserId = actingUserId,
            Status = WallStitchJobStatus.Queued,
            RequestedProjection = options.DefaultProjection,
            WallWidthM = options.WallWidthM,
            WallHeightM = options.WallHeightM,
            NaturalProjection = options.Natural,
            Curve = options.Curve,
            TransferHolds = options.TransferHolds,
            PhotoCount = photos.Count,
        };

        var wireOptions = await WallStitchRequestBuilder.BuildOptionsAsync(db, wallId, options, ct);
        var oldPhoto = options.TransferHolds ? await WallStitchRequestBuilder.LoadOldPhotoAsync(db, wallId, ct) : null;
        if (options.TransferHolds && oldPhoto is null)
        {
            throw new InvalidOperationException($"Wall {wallId} has no current photo, so holds cannot be transferred.");
        }

        var created = await client.CreateJobAsync(photos, wireOptions, oldPhoto, ct);
        job.SidecarJobId = created.JobId;
        job.Status = StitchWire.ParseStatus(created.Status);

        db.WallStitchJobs.Add(job);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Started stitch job {JobId} (sidecar {SidecarJobId}) for wall {WallId}", job.Id, created.JobId, wallId);
        return job;
    }

    public async Task<WallStitchJob?> GetJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        return await db.WallStitchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
    }

    public async Task<IReadOnlyList<WallStitchJob>> GetJobsForWallAsync(Guid wallId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        return await db.WallStitchJobs
            .AsNoTracking()
            .Where(j => j.WallId == wallId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<WallStitchJob?> RefreshJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var job = await db.WallStitchJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status.IsTerminal() || string.IsNullOrEmpty(job.SidecarJobId))
        {
            return job;
        }

        var state = await client.GetJobAsync(job.SidecarJobId, ct);
        ApplyState(job, state);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<StitchJobResult?> GetResultAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        if (job?.SidecarJobId is null || job.Status != WallStitchJobStatus.Succeeded)
        {
            return null;
        }

        var state = await client.GetJobAsync(job.SidecarJobId, ct);
        return state.Result;
    }

    public async Task CancelJobAsync(Guid jobId, Guid actingUserId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var job = await db.WallStitchJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            return;
        }

        await WallAdminGuard.EnsureWallAdminAsync(db, job.WallId, actingUserId, ct);

        if (!string.IsNullOrEmpty(job.SidecarJobId))
        {
            await client.DeleteJobAsync(job.SidecarJobId, ct);
        }

        job.Status = WallStitchJobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(string FlatMasterPath, string NaturalMasterPath)> DownloadMastersAsync(
        Guid jobId,
        CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        var result = await GetResultAsync(jobId, ct);
        if (job?.SidecarJobId is null || result is null)
        {
            throw new InvalidOperationException($"Stitch job {jobId} has no downloadable result.");
        }

        var flat = await DownloadArtifactToStoreAsync(job.SidecarJobId, result.FlatMaster.Artifact, ct);
        var natural = await DownloadArtifactToStoreAsync(job.SidecarJobId, result.NaturalMaster.Artifact, ct);
        return (flat, natural);
    }

    /// <inheritdoc/>
    public async Task ApplyResultToStagingAsync(Guid jobId, Guid actingUserId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct)
                  ?? throw new InvalidOperationException($"Stitch job {jobId} does not exist.");

        if (job.Status != WallStitchJobStatus.Succeeded)
        {
            throw new InvalidOperationException($"Stitch job {jobId} is {job.Status}, so it has no result to apply.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        await WallAdminGuard.EnsureWallAdminAsync(db, job.WallId, actingUserId, ct);

        var result = await GetResultAsync(jobId, ct)
                     ?? throw new InvalidOperationException($"Stitch job {jobId} has no downloadable result.");
        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == job.WallId, ct)
                   ?? throw new InvalidOperationException($"Wall {job.WallId} not found.");

        var isFlatDefault = job.RequestedProjection == WallPhotoProjection.Flat;
        var defaultImage = await DownloadDisplayAsync(job.SidecarJobId!, isFlatDefault ? result.DisplayFlat : result.DisplayNatural, ct);
        var alternateImage = await DownloadDisplayAsync(job.SidecarJobId!, isFlatDefault ? result.DisplayNatural : result.DisplayFlat, ct);
        var (flatMaster, naturalMaster) = await DownloadMastersAsync(jobId, ct);
        var camerasJson = await DownloadCamerasAsync(job.SidecarJobId!, result, ct);
        var curvatureJson = result.Curvature is null
            ? null
            : JsonSerializer.Serialize(result.Curvature, WallStitchClient.Json);

        var summary = await WallStitchStagingApplier.CloneHoldsAsync(db, wall, result, ct);
        var retired = WallStitchStagingApplier.ApplyPhoto(
            wall,
            job,
            result,
            defaultImage,
            alternateImage,
            flatMaster,
            naturalMaster,
            camerasJson,
            curvatureJson);

        await db.SaveChangesAsync(ct);
        await WallPhotoMasterCleanup.DeleteUnreferencedAsync(db, masterStorage, retired, ct);

        logger.LogInformation(
            "Stitch job {JobId} applied to wall {WallId} staging: {Total} staged hold(s) ({CarriedOver} carried over, {Missing} missing, {New} new, {Unreported} unreported), {NeedsReview} needing review; carryover blocker: {Blocker}",
            jobId,
            job.WallId,
            summary.Total,
            summary.CarriedOver,
            summary.Missing,
            summary.New,
            summary.Unreported,
            summary.NeedsReview,
            string.IsNullOrEmpty(summary.Blocker) ? "none" : summary.Blocker);
    }

    /// <summary>
    /// Pulls the pipeline's <c>cameras.json</c> as text, or null when the job did not emit one.
    /// Small enough to buffer, unlike the masters.
    /// </summary>
    private async Task<string?> DownloadCamerasAsync(string sidecarJobId, StitchJobResult result, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(result.CamerasJson))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await client.DownloadArtifactAsync(sidecarJobId, result.CamerasJson, buffer, ct);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<byte[]> DownloadDisplayAsync(string sidecarJobId, string artifact, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await client.DownloadArtifactAsync(sidecarJobId, artifact, buffer, ct);
        return buffer.ToArray();
    }

    private async Task<string> DownloadArtifactToStoreAsync(string sidecarJobId, string artifact, CancellationToken ct)
    {
        var extension = Path.GetExtension(artifact);
        var tempPath = masterStorage.CreateTempPath(extension);
        try
        {
            await using (var file = File.Create(tempPath))
            {
                await client.DownloadArtifactAsync(sidecarJobId, artifact, file, ct);
            }

            return masterStorage.Commit(tempPath, extension);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    private static void ApplyState(WallStitchJob job, StitchJobState state)
    {
        var status = StitchWire.ParseStatus(state.Status);
        if (status != WallStitchJobStatus.Queued && job.StartedAt is null)
        {
            job.StartedAt = DateTimeOffset.UtcNow;
        }

        job.Status = status;
        job.Progress = state.Progress;
        job.Stage = Truncate(state.Stage, 64);
        job.ErrorCode = Truncate(state.Error?.Code, 64);
        job.ErrorMessage = Truncate(state.Error?.Message, 1024);

        if (state.Result?.Diagnostics is not null)
        {
            job.DiagnosticsJson = JsonSerializer.Serialize(state.Result.Diagnostics, WallStitchClient.Json);
        }

        if (status.IsTerminal())
        {
            job.CompletedAt ??= DateTimeOffset.UtcNow;
            job.Progress = status == WallStitchJobStatus.Succeeded ? 1.0 : job.Progress;
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
