using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// DEVELOPMENT-ONLY admin harness for the change journal's revert / export / replay operations. The
/// intended workflow: run a wall update ONCE locally, verify it, export the recorded batch, then
/// replay that exact package onto prod — so the fiddly update never has to be redone by hand.
/// <para>
/// Mapped ONLY inside the <c>app.Environment.IsDevelopment()</c> block in <c>Program.cs</c>, next to
/// the other dev harness endpoints (<see cref="DevWallUpdateEndpoints"/>, <see cref="DevAuthEndpoints"/>).
/// Every handler additionally re-checks the environment as defence in depth; there is no other gate.
/// It MUST NEVER be enabled in Production.
/// </para>
/// </summary>
internal static class ChangeJournalDevEndpoints
{
    public static void MapDevJournal(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/dev/journal");

        // GET /dev/journal/export?batchId=<guid>&batchId=<guid> — the portable package JSON.
        group.MapGet("/export", ExportAsync);

        // POST /dev/journal/replay?dryRun=true&force=false — body is the package; returns the report.
        group.MapPost("/replay", ReplayAsync);

        // POST /dev/journal/revert?batchId=<guid>&force=false — inverse-applies a captured batch.
        group.MapPost("/revert", RevertAsync);
    }

    private static async Task<IResult> ExportAsync(
        HttpContext http,
        IHostEnvironment environment,
        ChangeJournalExporter exporter,
        [FromQuery] Guid[]? batchId)
    {
        if (!environment.IsDevelopment())
        {
            return Results.NotFound();
        }

        if (batchId is null || batchId.Length == 0)
        {
            return Results.BadRequest(new { error = "Provide at least one ?batchId=<guid>." });
        }

        var package = await exporter.ExportBatchesAsync(batchId, http.RequestAborted);
        return Results.Json(package);
    }

    private static async Task<IResult> ReplayAsync(
        HttpContext http,
        IHostEnvironment environment,
        ChangeJournalReplayer replayer,
        [FromBody] ChangeJournalPackage? package,
        [FromQuery] bool? dryRun,
        [FromQuery] bool? force)
    {
        if (!environment.IsDevelopment())
        {
            return Results.NotFound();
        }

        if (package is null)
        {
            return Results.BadRequest(new { error = "Send the exported package JSON as the request body." });
        }

        var report = await replayer.ImportReplayAsync(
            package, dryRun ?? true, force ?? false, http.RequestAborted);
        return Results.Json(report);
    }

    private static async Task<IResult> RevertAsync(
        HttpContext http,
        IHostEnvironment environment,
        ChangeJournalReverter reverter,
        [FromQuery] Guid batchId,
        [FromQuery] bool? force)
    {
        if (!environment.IsDevelopment())
        {
            return Results.NotFound();
        }

        var result = await reverter.RevertBatchAsync(
            batchId, force: force ?? false, cancellationToken: http.RequestAborted);
        return Results.Json(result);
    }
}
