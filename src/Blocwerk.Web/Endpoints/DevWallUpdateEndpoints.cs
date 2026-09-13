using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// DEVELOPMENT-ONLY HTTP harness that drives the whole big-wall update (stage photos → run the
/// OpenCV/YOLO carryover matcher → promote / discard) via API calls, so the matcher can be iterated
/// on without the Blazor UI. Wired only when <c>app.Environment.IsDevelopment()</c>; no auth — the
/// mutating routes impersonate the wall's OWNER (via <see cref="DevOwnerCurrentUserService"/>) so the
/// real service's <see cref="WallAdminGuard"/> passes exactly as it would for the signed-in owner.
/// Reads (metrics) query the DB directly.
/// </summary>
internal static class DevWallUpdateEndpoints
{
    public static void MapDevWallUpdate(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/dev/walls/{wallId:guid}");

        group.MapPost("/big-update/run", RunAsync);
        group.MapPost("/big-update/promote", PromoteAsync);
        group.MapPost("/big-update/discard", DiscardAsync);
        group.MapGet("/metrics", MetricsAsync);
    }

    // 1. Stage the uploaded photos onto their (col,row) panels and build the carryover session
    //    (this runs the matcher) — replicating BigWallUpdate.razor.cs OnUpload → BigUpdate.StartAsync.
    private static async Task<IResult> RunAsync(Guid wallId, HttpContext http)
    {
        var ctx = await DevWallUpdateSupport.BuildServiceAsync(http, wallId);
        if (ctx is null)
        {
            return Results.NotFound(new { error = "Wall not found." });
        }

        var (service, _, factory) = ctx;

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var cols = form["col"];
        var rows = form["row"];
        if (form.Files.Count == 0 || cols.Count != form.Files.Count || rows.Count != form.Files.Count)
        {
            return Results.BadRequest(new
            {
                error = "Send one photo file per part, each with a matching col and row form field "
                    + "(e.g. -F photo=@centre.jpg -F col=0 -F row=0 -F photo=@right.jpg -F col=1 -F row=0).",
            });
        }

        var photos = new List<BigUpdatePhoto>(form.Files.Count);
        for (var i = 0; i < form.Files.Count; i++)
        {
            var file = form.Files[i];
            if (!int.TryParse(cols[i], out var col) || !int.TryParse(rows[i], out var row))
            {
                return Results.BadRequest(new { error = $"col/row #{i} are not integers." });
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, http.RequestAborted);
            var contentType = string.IsNullOrEmpty(file.ContentType) ? "image/jpeg" : file.ContentType;
            photos.Add(new BigUpdatePhoto(ms.ToArray(), contentType, col, row));
        }

        try
        {
            // StartAsync itself discards any prior in-flight staged update for the wall (idempotent
            // restart), stages every panel, detects holds, and runs the carryover matcher.
            var session = await service.StartAsync(wallId, photos);
            DevBigUpdateStore.Set(wallId, session);
            return Results.Json(await DevWallUpdateSupport.BuildRunResponseAsync(factory, wallId, session));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // 2. Promote the in-flight staged session, carrying its proposals as the default (carry-all,
    //    accept every genuinely-new centre hold, link every neighbour proposal) — the UI's "confirm".
    private static async Task<IResult> PromoteAsync(Guid wallId, HttpContext http)
    {
        var session = DevBigUpdateStore.Get(wallId);
        if (session is null)
        {
            return Results.BadRequest(new { error = "No in-flight run for this wall — POST /big-update/run first." });
        }

        var ctx = await DevWallUpdateSupport.BuildServiceAsync(http, wallId);
        if (ctx is null)
        {
            return Results.NotFound(new { error = "Wall not found." });
        }

        var (service, _, factory) = ctx;

        var confirmation = await DevWallUpdateSupport.BuildCarryAllConfirmationAsync(factory, session);

        // The revision KPI is a PREDICTIVE score — measure it before promote re-points the boulders.
        RevisionForecast revision;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CurrentUserId = Guid.Empty;
            revision = await DevWallUpdateSupport.ComputeRevisionForecastAsync(db, session.RemovedCandidateHoldIds);
        }

        try
        {
            await service.PromoteAsync(wallId, confirmation);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        DevBigUpdateStore.Clear(wallId);
        return Results.Json(await DevWallUpdateSupport.BuildPromoteResponseAsync(factory, wallId, session.CenterPanelId, revision));
    }

    // 3. Discard the staged update.
    private static async Task<IResult> DiscardAsync(Guid wallId, HttpContext http)
    {
        var ctx = await DevWallUpdateSupport.BuildServiceAsync(http, wallId);
        if (ctx is null)
        {
            return Results.NotFound(new { error = "Wall not found." });
        }

        var service = ctx.Service;

        try
        {
            await service.DiscardAsync(wallId);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        DevBigUpdateStore.Clear(wallId);
        return Results.Json(new { discarded = true });
    }

    // 4. Per-generation hold counts, boulder review state, and the panel grid — read straight from DB.
    private static async Task<IResult> MetricsAsync(
        Guid wallId,
        IDbContextFactory<BlocwerkDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var holdsByGeneration = await db.Holds
            .Where(h => h.WallId == wallId)
            .GroupBy(h => h.Generation)
            .Select(g => new { generation = g.Key, count = g.Count() })
            .OrderBy(g => g.generation)
            .ToListAsync();

        var bouldersTotal = await db.Boulders.CountAsync(b => b.WallId == wallId && !b.IsArchived);
        var bouldersNeedingReview = await db.Boulders
            .CountAsync(b => b.WallId == wallId && !b.IsArchived && !b.IsHistoric && b.NeedsReview);
        var bouldersHistoric = await db.Boulders
            .CountAsync(b => b.WallId == wallId && !b.IsArchived && b.IsHistoric);

        var panels = await db.WallPanels
            .Where(p => p.WallId == wallId)
            .OrderBy(p => p.Generation).ThenBy(p => p.Col).ThenBy(p => p.Row)
            .Select(p => new
            {
                col = p.Col,
                row = p.Row,
                generation = p.Generation,
                hasPhoto = p.Photo != null,
                hasStaged = p.StagedPhoto != null,
            })
            .ToListAsync();

        return Results.Json(new
        {
            holdsByGeneration,
            boulders = new { total = bouldersTotal, needsReview = bouldersNeedingReview, historic = bouldersHistoric },
            panels,
        });
    }
}
