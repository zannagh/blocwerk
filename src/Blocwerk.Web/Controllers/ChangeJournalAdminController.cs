// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.EntityFrameworkCore;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The change journal's production replay path (<see cref="IChangeJournalAdminService"/>): list a wall's batches, export
/// them as a gzip package, replay a package (dry run unless <c>dryRun=false</c>), revert a batch (dry run likewise).
/// Machine routes for the rollout script, authorised exactly like the capture packages: the scheme is pinned to the API
/// key, the policy admits a PERSONAL key with write access only, and the service requires the key's owner to be an app
/// administrator. Replaces the Development-only <c>/dev/journal/*</c> harness for prod.
/// </summary>
[ApiController]
[Route("api/v1/admin/journal")]
[Authorize(Policy = BlocwerkPolicies.HumanOrUserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public sealed class ChangeJournalAdminController(IChangeJournalAdminService journal, ILogger<ChangeJournalAdminController> logger)
    : ControllerBase
{
    /// <summary>A package carries every photo it references as Base64: a few hundred MB must fit.</summary>
    public const long MaxPackageBytes = 1024L * 1024 * 1024;

    private const int ClientClosedRequest = 499;

    private static readonly MinDataRate MinBodyRate = new(bytesPerSecond: 16 * 1024, gracePeriod: TimeSpan.FromSeconds(30));

    /// <summary>The wall's batches, oldest first; <c>includeUnscoped</c> adds the "adhoc" ones that touched it since <c>since</c> (default 7 days).</summary>
    [HttpGet("walls/{wallId:guid}/batches")]
    public Task<IActionResult> ListBatches(
        Guid wallId, [FromQuery] bool includeUnscoped = false, [FromQuery] DateTimeOffset? since = null, CancellationToken ct = default) =>
        RunAsync(async () => Ok(await journal.ListBatchesAsync(wallId, includeUnscoped, since, ct)));

    /// <summary>
    /// A package as gzip-compressed JSON (photos inside), in write order. Exactly one selection: <c>batchId</c> (repeat
    /// it) for chosen batches; <c>updateBatchId</c> for a whole wall update (its wall-update batch and every batch created
    /// while it was open, adhoc ones included); or <c>wallId</c> with <c>since</c>/<c>until</c> for a time window of the
    /// wall's batches (<c>includeUnscoped</c>, default true, adds the adhoc ones that touched it). Replays and reverts are
    /// left out of the latter two.
    /// </summary>
    [HttpGet("export")]
    public Task<IActionResult> Export(
        [FromQuery] Guid[]? batchId,
        [FromQuery] Guid? updateBatchId,
        [FromQuery] Guid? wallId,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] DateTimeOffset? until,
        [FromQuery] bool includeUnscoped = true,
        CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var modes = (batchId is { Length: > 0 } ? 1 : 0) + (updateBatchId is null ? 0 : 1) + (wallId is null ? 0 : 1);
            if (modes != 1)
            {
                return BadRequest(new ApiErrorResponse("Name exactly one of ?batchId=<guid>(&batchId=…), ?updateBatchId=<guid> or ?wallId=<guid>(&since=&until=)."));
            }

            var package = updateBatchId is { } update
                ? await journal.ExportWallUpdateAsync(update, ct)
                : wallId is { } wall
                    ? await journal.ExportWallAsync(wall, since, until, includeUnscoped, ct)
                    : await journal.ExportAsync(batchId!, ct);
            if (package is null)
            {
                return NotFound(new ApiErrorResponse("That is not a wall-update batch."));
            }

            var file = new FileStream(
                Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await ChangeJournalPackageJson.WriteGzipAsync(package, file, ct);
            file.Position = 0;
            return File(file, "application/gzip", $"journal-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json.gz");
        });

    /// <summary>
    /// Replays a package (gzip or plain JSON body). 200 with the report for a dry run (conflicts inside) or a committed
    /// run; 409 with the report when a real run was refused (a conflict without <c>force</c>, or a fatal error such as a
    /// schema mismatch, which <c>force</c> never skips).
    /// </summary>
    [HttpPost("replay")]
    [RequestSizeLimit(MaxPackageBytes)]
    public Task<IActionResult> Replay([FromQuery] bool dryRun = true, [FromQuery] bool force = false, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            // Per-request rates are HTTP/1.x only (Kestrel throws for HTTP/2; Caddy proxies over HTTP/1.1).
            if (HttpProtocol.IsHttp11(Request.Protocol) && HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
            {
                rate.MinDataRate = MinBodyRate;
            }

            // Refuse a non-admin before reading (and spooling) a body of up to a gigabyte.
            await journal.EnsureAdminAsync(ct);

            // Spooled to a temp file past the in-memory threshold, so the body can be sniffed for gzip and re-read.
            Request.EnableBuffering(bufferThreshold: 1024 * 1024, bufferLimit: MaxPackageBytes);
            var package = await ChangeJournalPackageJson.ReadAsync(Request.Body, ct);
            var report = await journal.ReplayAsync(package, dryRun, force, ct);
            if (report.ReplayBatchId is { } replayBatch)
            {
                // The batch the replayed writes landed in: what to revert if the replay must be undone.
                Response.Headers["X-Replay-Batch-Id"] = replayBatch.ToString();
            }

            return (report.DryRun && report.FatalErrors.Count == 0) || report.Applied ? Ok(report) : Conflict(report);
        });

    /// <summary>
    /// Reverts one batch. 200 with the result for a dry run (conflicts inside) or a done revert; 404 for an unknown
    /// batch; 409 with the conflicts when a real revert was refused.
    /// </summary>
    [HttpPost("batches/{batchId:guid}/revert")]
    public Task<IActionResult> Revert(
        Guid batchId, [FromQuery] bool dryRun = true, [FromQuery] bool force = false, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var result = await journal.RevertAsync(batchId, dryRun, force, ct);
            if (result.Error is not null)
            {
                return NotFound(result);
            }

            return result.DryRun || result.Reverted ? Ok(result) : Conflict(result);
        });

    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ApiErrorResponse("Only an administrator of this installation may export, replay or revert journal batches."));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return BadRequest(new ApiErrorResponse($"The package is not readable (gzip or plain JSON expected): {ex.Message}"));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ApiErrorResponse($"The package is larger than {MaxPackageBytes / (1024 * 1024)} MB."));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; a replay's transaction rolled back with it. 499, as nginx and Caddy log it.
            return StatusCode(ClientClosedRequest, new ApiErrorResponse("The request was cancelled; nothing was committed."));
        }
        catch (DbUpdateException ex)
        {
            // The database refused a write while applying (a constraint the package's rows break here). The
            // transaction rolled back, so nothing changed; the inner message names the constraint.
            logger.LogWarning(ex, "Journal admin request hit a database refusal");
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The database refused a write; nothing was committed.",
                detail: ex.InnerException?.Message ?? ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Journal admin request failed unexpectedly");
            return Conflict(new ApiErrorResponse(UserFacingException.GenericMessage));
        }
    }
}
