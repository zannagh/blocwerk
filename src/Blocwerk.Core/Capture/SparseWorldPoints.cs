// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Sparse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>The sparse points of a model's capture in its wall world, and how they were tied to it.</summary>
/// <param name="Points">World points, mm (reliable ones only).</param>
/// <param name="Alignment">The fit to the model's cameras.</param>
public sealed record SparseWorldPointSet(List<(float X, float Y, float Z)> Points, SparseAlignment Alignment);

/// <summary>
/// The evidence for volumes and protrusion when a model has no photo-real view: the sparse points kept from the
/// reconstruction of the capture that produced it (<see cref="Entities.WallCapture.SparsePointsStoredPath"/>), moved into
/// the model's wall world through the photo camera centres (<see cref="SparseWorldAlignment"/>).
/// </summary>
public static class SparseWorldPoints
{
    /// <summary>The points of <paramref name="modelId"/>'s capture in its world, or null (none kept, unreadable, or no fit).</summary>
    /// <param name="db">A context.</param>
    /// <param name="files">The capture store.</param>
    /// <param name="modelId">The model (the capture row points at the live member of its family).</param>
    /// <param name="modelJson">Its JSON (for the cameras).</param>
    /// <param name="logger">For the fit and why there is none.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The points, or null.</returns>
    public static async Task<SparseWorldPointSet?> LoadAsync(
        BlocwerkDbContext db, ICaptureFileStore files, Guid modelId, string modelJson, ILogger logger, CancellationToken ct)
    {
        var stored = await db.WallCaptures.AsNoTracking()
            .Where(c => c.GeometryModelId == modelId && c.SparsePointsStoredPath != null)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.SparsePointsStoredPath)
            .FirstOrDefaultAsync(ct);
        if (stored is null || await files.ReadAsync(stored, ct) is not { } bytes)
        {
            return null;
        }

        SparseCloud cloud;
        try
        {
            cloud = SparseCloudFile.Read(bytes);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("The sparse points of model {ModelId} are unreadable: {Reason}", modelId, ex.Message);
            return null;
        }

        if (SparseWorldAlignment.Fit(cloud.PhotoCentres, SolvedCamera.ParseAll(modelJson)) is not { } alignment)
        {
            logger.LogInformation("The sparse points of model {ModelId} could not be tied to its cameras", modelId);
            return null;
        }

        var points = SparseWorldAlignment.WorldPoints(cloud, alignment);
        logger.LogInformation(
            "Sparse points of model {ModelId}: {Points} of {Total} kept, tied on {Photos} photos (rms {Rms} mm)",
            modelId, points.Count, cloud.Count, alignment.Used, alignment.RmsMm);
        return new SparseWorldPointSet(points, alignment);
    }
}
