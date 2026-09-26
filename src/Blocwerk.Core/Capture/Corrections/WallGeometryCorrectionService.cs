// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>
/// <see cref="IWallGeometryCorrectionService"/>: reads the active model's sources for the result card and turns each
/// correction into a new model version (see the partial files for the corrections and the saving).
/// </summary>
/// <remarks><paramref name="kioskContext"/> is optional, as on <c>WallGlyphService</c>.</remarks>
public sealed partial class WallGeometryCorrectionService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    CorrectionFollowUpQueue followUps,
    ILogger<WallGeometryCorrectionService> logger,
    IKioskContext? kioskContext = null) : IWallGeometryCorrectionService
{
    private const string AdminAction = "Correcting a wall's 3D model";

    public async Task<GeometryCorrectionState?> GetStateAsync(Guid wallId)
    {
        var (db, _) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var model = await db.WallGeometryModels.AsNoTracking().FirstOrDefaultAsync(m => m.WallId == wallId && m.IsActive);
            if (model is null || Parse(model.Json) is not { } document)
            {
                return null;
            }

            var root = JsonNode.Parse(model.Json)!;
            var carriedFrom = await CarriedFromAsync(db, root);
            var photos = await PhotosWithCamerasAsync(db, model.Id, model.Json);
            var reference = Text(root["world"], "referenceFacet") ?? "0";
            var vertical = Text(root["world"], "verticalFacet");
            var facets = document.Segments
                .SelectMany(s => s.Facets.Select(f => new GeometryCorrectionFacet(
                    f.Id, string.IsNullOrWhiteSpace(s.Name) ? $"Surface {f.Id}" : s.Name!, f.MeasuredAngleDeg, f.Id == reference, f.Id == vertical)))
                .ToList();
            return new GeometryCorrectionState(
                model.Id,
                model.CreatedAt,
                document.IsFeatureFrame,
                GeometrySourceText.Scale(document.World, carriedFrom),
                document.World?.ScaleIsEstimate == true,
                GeometrySourceText.Gravity(document.World, carriedFrom),
                !GeometrySourceText.GravityUnknown(document.World),
                photos.CaptureId,
                photos.Photos,
                facets,
                Text(root["quality"]?["correction"], "summary"));
        }
    }

    private static WallGeometryDocument? Parse(string json)
    {
        try
        {
            return WallGeometryDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node, string key) =>
        Geometry.Corrections.GeometryJson.Text(node, key);

    /// <summary>When the model the scale and "up" were carried over from (anchors) was made, if it is still stored.</summary>
    private static async Task<DateTimeOffset?> CarriedFromAsync(BlocwerkDbContext db, JsonNode root)
    {
        var id = Text(root["quality"]?["registration"], "referenceModelId");
        if (!Guid.TryParse(id, out var referenceId))
        {
            return null;
        }

        return await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.Id == referenceId)
            .Select(m => (DateTimeOffset?)m.CreatedAt)
            .FirstOrDefaultAsync();
    }

    /// <summary>The finished capture that points at the model, and its stored photos that have a solved camera.</summary>
    private static async Task<(Guid? CaptureId, IReadOnlyList<CapturePhotoResult> Photos)> PhotosWithCamerasAsync(
        BlocwerkDbContext db, Guid modelId, string modelJson)
    {
        var captureId = await db.WallCaptures.AsNoTracking()
            .Where(c => c.GeometryModelId == modelId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();
        if (captureId is null)
        {
            return (null, []);
        }

        var cameras = CameraNames(modelJson);
        var photos = await db.WallCapturePhotos.AsNoTracking()
            .Where(p => p.CaptureId == captureId)
            .OrderBy(p => p.Index)
            .ToListAsync();
        var usable = photos
            .Where(p => cameras.Contains(CaptureComputeDocuments.PhotoName(p.Index)))
            .Select(p => new CapturePhotoResult(p.Id, p.Index, p.OriginalFileName, p.Width, p.Height, p.Focal35mm, [], []))
            .ToList();
        return (usable.Count > 0 ? captureId : null, usable);
    }

    private static HashSet<string> CameraNames(string modelJson) =>
        SolvedCamera.ParseAll(modelJson).Select(c => c.Image).ToHashSet(StringComparer.Ordinal);

    /// <summary>An admin context of the wall; refused from a kiosk. The caller disposes it.</summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync();
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
