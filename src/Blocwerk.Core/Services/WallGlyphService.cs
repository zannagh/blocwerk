using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Glyph (ArUco marker) settings and geometry models for a wall. Writes use the same gate as every
/// other wall-admin action — <see cref="WallAdminGuard"/> (owner or admin, pinned to a kiosk's own
/// wall) plus <see cref="KioskGuard.EnsureNotKiosk(IKioskContext?, BlocwerkDbContext, string)"/>,
/// because declaring hardware and importing a solve are owner-desk tasks, never tablet tasks.
/// </summary>
/// <remarks><paramref name="kioskContext"/> is optional, as on <c>WallService</c>.</remarks>
public partial class WallGlyphService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    ILogger<WallGlyphService> logger,
    IKioskContext? kioskContext = null) : IWallGlyphService
{
    public async Task<WallGlyphSettings> GetGlyphSettingsAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        // The Wall query filter is the visibility gate: a wall the user cannot see is "not found".
        var settings = await db.Walls
            .Where(w => w.Id == wallId)
            .Select(w => new WallGlyphSettings(w.GlyphsEnabled, w.MarkerSizeMm))
            .FirstOrDefaultAsync();
        return settings ?? throw new InvalidOperationException("Wall not found");
    }

    public async Task<WallGlyphSettings> SetGlyphSettingsAsync(Guid wallId, bool enabled, double? markerSizeMm)
    {
        if (markerSizeMm is { } size && (!double.IsFinite(size) || size <= 0 || size > WallGeometryValidator.MaxMarkerSizeMm))
        {
            throw new ArgumentOutOfRangeException(
                nameof(markerSizeMm), $"Marker size must be between 0 and {WallGeometryValidator.MaxMarkerSizeMm:0} mm.");
        }

        var (db, userId) = await OpenForAdminWriteAsync(wallId, "Changing a wall's marker settings");
        await using (db)
        {
            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                       ?? throw new InvalidOperationException("Wall not found");
            wall.GlyphsEnabled = enabled;
            if (markerSizeMm is not null)
            {
                wall.MarkerSizeMm = markerSizeMm;
            }

            await db.SaveChangesAsync();
            logger.LogInformation(
                "Wall {WallId} markers {State} (size {MarkerSizeMm} mm) by {UserId}",
                wallId, enabled ? "enabled" : "disabled", wall.MarkerSizeMm, userId);
            return new WallGlyphSettings(wall.GlyphsEnabled, wall.MarkerSizeMm);
        }
    }

    public async Task<ActiveWallGeometry?> GetActiveGeometryAsync(Guid wallId)
    {
        var (db, _) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var model = await db.WallGeometryModels
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.WallId == wallId && m.IsActive);
            if (model is null)
            {
                return null;
            }

            WallGeometryDocument document;
            try
            {
                document = WallGeometryDocument.Parse(model.Json);
            }
            catch (JsonException ex)
            {
                // Validated on import, so this is a stored row gone bad; show the model, not a crash.
                logger.LogWarning(ex, "Active geometry model {ModelId} of wall {WallId} no longer parses", model.Id, wallId);
                return new ActiveWallGeometry(ToEntry(model), 0, 0, []);
            }

            return new ActiveWallGeometry(
                ToEntry(model),
                document.Markers.Count,
                document.MarkerSizeMm,
                WallGeometrySummary.FacetRows(document),
                WallGeometryModelChecks.From(document));
        }
    }

    public async Task<IReadOnlyList<WallGeometryHistoryEntry>> GetGeometryHistoryAsync(Guid wallId)
    {
        var (db, _) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            // Ordered on the entity, before the positional-record projection, so it translates.
            return await db.WallGeometryModels
                .AsNoTracking()
                .Where(m => m.WallId == wallId)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new WallGeometryHistoryEntry(
                    m.Id, m.CreatedAt, m.IsActive, m.SchemaVersion, m.Source, m.ReprojRmsPx, m.WidthMm, m.HeightMm, m.Notes))
                .ToListAsync();
        }
    }

    public async Task SetSegmentMarkerIndexAsync(Guid segmentId, int? markerSegmentIndex)
    {
        if (markerSegmentIndex is < 0 or > MaxMarkerSegmentIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(markerSegmentIndex), $"Marker segment must be between 0 and {MaxMarkerSegmentIndex}.");
        }

        var wallId = await ResolveSegmentWallAsync(segmentId);
        var (db, userId) = await OpenForAdminWriteAsync(wallId, "Binding a segment to markers");
        await using (db)
        {
            var segments = await db.WallSegments.Where(s => s.WallId == wallId).ToListAsync();
            var segment = segments.First(s => s.Id == segmentId);
            var clash = segments.FirstOrDefault(s => s.Id != segmentId && markerSegmentIndex is not null
                                                     && s.MarkerSegmentIndex == markerSegmentIndex);
            if (clash is not null)
            {
                throw new InvalidOperationException(
                    $"Marker segment {markerSegmentIndex} is already bound to segment \"{clash.Name}\".");
            }

            segment.MarkerSegmentIndex = markerSegmentIndex;
            segment.MeasuredAngle = null;
            segment.MeasuredYaw = null;
            var document = await LoadActiveDocumentAsync(db, wallId);
            if (document is not null)
            {
                WallGeometrySummary.ApplyMeasured(document, [segment]);
            }

            await db.SaveChangesAsync();
            logger.LogInformation(
                "Segment {SegmentId} of wall {WallId} bound to marker segment {Index} by {UserId}",
                segmentId, wallId, markerSegmentIndex, userId);
        }
    }

    private async Task<Guid> ResolveSegmentWallAsync(Guid segmentId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        var wallId = await db.WallSegments
            .Where(s => s.Id == segmentId)
            .Select(s => (Guid?)s.WallId)
            .FirstOrDefaultAsync();
        return wallId ?? throw new InvalidOperationException("Segment not found");
    }

    /// <summary>
    /// A context for an admin READ of <paramref name="wallId"/>. The caller owns (disposes) it; it is
    /// disposed here when the check fails.
    /// </summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId, string? kioskRefusal = null)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync();
        try
        {
            db.CurrentUserId = user.Id;
            if (kioskRefusal is not null)
            {
                KioskGuard.EnsureNotKiosk(kioskContext, db, kioskRefusal);
            }

            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>As <see cref="OpenForAdminAsync"/>, and refused outright from any kiosk tablet.</summary>
    private Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminWriteAsync(Guid wallId, string action) =>
        OpenForAdminAsync(wallId, action);

    private static WallGeometryHistoryEntry ToEntry(WallGeometryModel m) => new(
        m.Id, m.CreatedAt, m.IsActive, m.SchemaVersion, m.Source, m.ReprojRmsPx, m.WidthMm, m.HeightMm, m.Notes);
}
