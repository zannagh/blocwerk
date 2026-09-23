using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>Capture status/history for the admin, and the active model's textures for viewers.</summary>
public sealed partial class WallCaptureService
{
    public async Task<IReadOnlyList<WallCaptureSummary>> GetCapturesAsync(Guid wallId)
    {
        var (db, _) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var captures = await db.WallCaptures.AsNoTracking()
                .Where(c => c.WallId == wallId && c.Status != WallCaptureStatus.Draft)
                .OrderByDescending(c => c.CreatedAt)
                .Take(20)
                .ToListAsync();
            return await SummarizeAsync(db, captures);
        }
    }

    public async Task<WallCaptureSummary?> GetCaptureAsync(Guid captureId)
    {
        var (db, _, capture) = await OpenCaptureAsync(captureId);
        await using (db)
        {
            return (await SummarizeAsync(db, [capture]))[0];
        }
    }

    public async Task<IReadOnlyList<WallGeometryTextureInfo>> GetActiveTexturesAsync(Guid wallId, string? shareToken = null)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        if (!await CanViewWallAsync(db, wallId, shareToken))
        {
            return [];
        }

        var textures = await db.WallGeometryTextures.AsNoTracking()
            .Where(t => t.GeometryModel.WallId == wallId && t.GeometryModel.IsActive)
            .OrderBy(t => t.FacetId)
            .ToListAsync();
        var query = string.IsNullOrEmpty(shareToken) ? string.Empty : $"?token={Uri.EscapeDataString(shareToken)}";
        return textures.Select(t => new WallGeometryTextureInfo(
            t.GeometryModelId,
            t.FacetId,
            $"{TextureUrl(wallId, t.GeometryModelId, t.FacetId)}{query}",
            t.AMin,
            t.AMax,
            t.BMin,
            t.BMax,
            t.WidthPx,
            t.HeightPx)).ToList();
    }

    /// <summary>The texture byte route (served by the web layer under the wall-media policy).</summary>
    public static string TextureUrl(Guid wallId, Guid modelId, string facetId) =>
        $"/api/walls/{wallId}/geometry/{modelId}/textures/{Uri.EscapeDataString(facetId)}";

    /// <summary>
    /// The texture row behind the byte route. The CALLER has already passed the wall-view gate for
    /// <paramref name="wallId"/>; this only enforces that model and wall belong together.
    /// </summary>
    public static Task<WallGeometryTexture?> FindTextureAsync(
        BlocwerkDbContext db, Guid wallId, Guid modelId, string facetId, CancellationToken ct) =>
        db.WallGeometryTextures.AsNoTracking()
            .Where(t => t.GeometryModelId == modelId && t.FacetId == facetId && t.GeometryModel.WallId == wallId)
            .FirstOrDefaultAsync(ct);

    /// <summary>A matching share token, the wall query filter for the signed-in user, or the wall's own kiosk.</summary>
    /// <remarks>
    /// A kiosk tablet only ever sees its own wall, share token or not: the token check runs through the
    /// wall query filter with the member check neutralised (<see cref="BlocwerkDbContext.CurrentUserId"/>
    /// still empty) but the kiosk pin kept — as <c>ActivityLogService</c> does — and, for a context the
    /// kiosk factory did not stamp, the session's own kiosk wall is checked here as well.
    /// </remarks>
    private async Task<bool> CanViewWallAsync(BlocwerkDbContext db, Guid wallId, string? shareToken)
    {
        if (kioskContext is { IsKiosk: true } && KioskViewing.ViewableWallId(kioskContext) != wallId)
        {
            return false;
        }

        db.CurrentUserId = Guid.Empty;
        if (!string.IsNullOrEmpty(shareToken)
            && await db.Walls.AnyAsync(w => w.Id == wallId && w.ShareToken == shareToken))
        {
            return true;
        }

        try
        {
            db.CurrentUserId = (await currentUserService.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return kioskContext is not null && KioskViewing.AllowsAnonymousViewOf(kioskContext, wallId);
        }

        return await db.Walls.AnyAsync(w => w.Id == wallId);
    }

    private static async Task<List<WallCaptureSummary>> SummarizeAsync(BlocwerkDbContext db, List<WallCapture> captures)
    {
        var ids = captures.Select(c => c.Id).ToList();
        var counts = await db.WallCapturePhotos.Where(p => ids.Contains(p.CaptureId))
            .GroupBy(p => p.CaptureId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count);
        return captures.Select(c => new WallCaptureSummary(
            c.Id, c.CreatedAt, c.Status, c.Progress, c.Stage, c.Error, c.Notes,
            counts.GetValueOrDefault(c.Id), c.GeometryModelId, c.CompletedAt, ReadPlacementCheck(c.PlacementCheckJson))).ToList();
    }

    private static MarkerPlacementCheck? ReadPlacementCheck(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MarkerPlacementCheck>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
