using System.Text.Json;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Geometry import and activation: the "one active model per wall" swap lives here.</summary>
public partial class WallGlyphService
{
    /// <summary>Largest accepted upload, in characters (≈ bytes for this ASCII format).</summary>
    public const int MaxJsonLength = 2 * 1024 * 1024;

    /// <summary>Largest marker-segment index accepted when binding a segment.</summary>
    public const int MaxMarkerSegmentIndex = 999;

    private const int MaxNotesLength = 2048;

    public Task<GeometryImportResult> ImportGeometryAsync(Guid wallId, string json, string? notes) =>
        ImportGeometryAsync(wallId, json, notes, source: null);

    /// <summary>
    /// The import, with the model's <see cref="WallGeometryModel.Source"/> set by the caller (the capture
    /// pipeline tags its models so a resumed capture finds the one it already imported).
    /// </summary>
    public Task<GeometryImportResult> ImportGeometryAsync(Guid wallId, string json, string? notes, string? source) =>
        ImportGeometryAsync(wallId, json, notes, source, new GeometryImportOptions());

    /// <summary>
    /// The import with the plan revision the model was solved with, and whether it becomes the active model
    /// (a capture whose model could not be tied to the active one stores it inactive, for the admin to decide).
    /// </summary>
    public async Task<GeometryImportResult> ImportGeometryAsync(
        Guid wallId, string json, string? notes, string? source, GeometryImportOptions options)
    {
        var (db, userId) = await OpenForAdminWriteAsync(wallId, "Importing a wall geometry");
        await using (db)
        {
            var parsed = ParseAndValidate(json);
            if (parsed.Document is not { } document)
            {
                return GeometryImportResult.Fail(parsed.Errors);
            }

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                       ?? throw new InvalidOperationException("Wall not found");
            var span = WallGeometrySummary.MarkerSpan(document);
            var model = new WallGeometryModel
            {
                WallId = wallId,
                Json = json,
                SchemaVersion = document.Version,
                Source = source ?? $"upload (schema v{document.Version})",
                CreatedByUserId = userId,
                IsActive = options.Activate,
                PlanRevision = options.PlanRevision,
                FrameSource = document.IsFeatureFrame ? WallGeometryFrameSource.Features : WallGeometryFrameSource.Markers,
                DerivedFromModelId = options.DerivedFromModelId,
                WidthMm = span?.WidthMm,
                HeightMm = span?.HeightMm,
                ReprojRmsPx = document.Quality?.ReprojRmsPx,
                Notes = TrimNotes(notes),
            };

            if (!document.IsFeatureFrame)
            {
                // A feature model's marker size is only an echo of the request, not a measured sheet.
                wall.MarkerSizeMm ??= document.MarkerSizeMm;
            }

            if (options.Activate)
            {
                await SwapActiveModelAsync(db, wallId, document, keep: null, () => db.WallGeometryModels.Add(model));
            }
            else
            {
                db.WallGeometryModels.Add(model);
                await db.SaveChangesAsync();
            }

            logger.LogInformation(
                "Wall {WallId} geometry model {ModelId} imported by {UserId} ({Markers} markers, {Facets} facets)",
                wallId, model.Id, userId, document.Markers.Count, document.Segments.Sum(s => s.Facets.Count));
            return GeometryImportResult.Ok(model);
        }
    }

    public async Task ActivateGeometryAsync(Guid modelId)
    {
        var wallId = await ResolveModelWallAsync(modelId);
        var (db, userId) = await OpenForAdminWriteAsync(wallId, "Activating a wall geometry");
        await using (db)
        {
            var model = await db.WallGeometryModels.FirstAsync(m => m.Id == modelId);
            if (model.IsActive)
            {
                return;
            }

            WallGeometryDocument document;
            try
            {
                document = WallGeometryDocument.Parse(model.Json);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Geometry model {ModelId} of wall {WallId} no longer parses", modelId, wallId);
                throw new InvalidOperationException("This stored geometry can no longer be read, so it cannot be activated.");
            }

            await SwapActiveModelAsync(db, wallId, document, keep: modelId, () => model.IsActive = true);
            logger.LogInformation("Wall {WallId} geometry model {ModelId} activated by {UserId}", wallId, modelId, userId);
            if (await ModelFamily.RepointCaptureAsync(db, wallId, modelId) is { } captureId)
            {
                // A correction's model (or the one it was derived from) is live again: re-derive the holds on it.
                followUpQueue?.Enqueue(captureId);
            }
        }
    }

    /// <summary>
    /// Parses and validates an upload. Never throws: a malformed or unusable file becomes a list of
    /// messages for the admin.
    /// </summary>
    internal static (WallGeometryDocument? Document, IReadOnlyList<string> Errors) ParseAndValidate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, ["The file is empty."]);
        }

        if (json.Length > MaxJsonLength)
        {
            return (null, ["The file is larger than 2 MB — is it really a wall-geometry.json?"]);
        }

        WallGeometryDocument document;
        try
        {
            document = WallGeometryDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (near line {line + 1})" : string.Empty;
            return (null, [$"The file is not valid wall-geometry JSON{where}."]);
        }

        var errors = WallGeometryValidator.Validate(document);
        return errors.Count == 0 ? (document, []) : (null, errors);
    }

    /// <summary>
    /// Retires the wall's active model, runs <paramref name="activate"/> to put the new one in place,
    /// and copies <paramref name="document"/>'s measured angles onto the bound segments — all in ONE
    /// transaction.
    /// </summary>
    /// <remarks>
    /// The filtered unique index admits one active row per wall, and the store checks it per
    /// statement. A single SaveChanges would be atomic but NOT ordered: EF sorts same-table UPDATEs by
    /// primary key and knows nothing about the index filter, so "activate B" could run before
    /// "deactivate A" whenever B's id sorts first. So the retirement is flushed first and the rest
    /// follows in the same transaction — equally all-or-nothing, and deterministic.
    /// </remarks>
    internal static async Task SwapActiveModelAsync(
        BlocwerkDbContext db, Guid wallId, WallGeometryDocument document, Guid? keep, Action activate)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var active = await db.WallGeometryModels
            .Where(m => m.WallId == wallId && m.IsActive && m.Id != keep)
            .ToListAsync();
        foreach (var previous in active)
        {
            previous.IsActive = false;
        }

        await db.SaveChangesAsync();

        activate();
        var segments = await db.WallSegments.Where(s => s.WallId == wallId).ToListAsync();
        WallGeometrySummary.ApplyMeasured(document, segments);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>The active model's parsed document, or null when there is none or it no longer parses.</summary>
    private static async Task<WallGeometryDocument?> LoadActiveDocumentAsync(BlocwerkDbContext db, Guid wallId)
    {
        var json = await db.WallGeometryModels
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => m.Json)
            .FirstOrDefaultAsync();
        if (json is null)
        {
            return null;
        }

        try
        {
            return WallGeometryDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Guid> ResolveModelWallAsync(Guid modelId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var wallId = await db.WallGeometryModels
            .Where(m => m.Id == modelId)
            .Select(m => (Guid?)m.WallId)
            .FirstOrDefaultAsync();
        return wallId ?? throw new InvalidOperationException("Geometry model not found");
    }

    private static string? TrimNotes(string? notes)
    {
        var trimmed = notes?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxNotesLength ? trimmed : trimmed[..MaxNotesLength];
    }
}

/// <summary>How <see cref="WallGlyphService.ImportGeometryAsync(Guid, string, string?, string?, GeometryImportOptions)"/> stores a model.</summary>
/// <param name="PlanRevision">The marker plan revision it was solved with (null: legacy or unknown).</param>
/// <param name="Activate">False stores it as inactive history only.</param>
/// <param name="DerivedFromModelId">The model it was derived from, if any (<see cref="WallGeometryModel.DerivedFromModelId"/>).</param>
public sealed record GeometryImportOptions(int? PlanRevision = null, bool Activate = true, Guid? DerivedFromModelId = null);
