// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.Replay;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The import side, in three calls: begin with the manifest (the dry-run report; the import id is the capture id), put
/// each missing file (streamed, verified against the manifest's size and SHA-256), commit (one transaction, then the
/// capture is queued to finish its trained view). Re-running any of them is safe.
/// </summary>
[Route("api/v1/admin/capture-imports")]
public sealed class CaptureImportsController(ICapturePackageService packages, ILogger<CaptureImportsController> logger)
    : CapturePackageApiController(packages, logger)
{
    /// <summary>The manifest carries the model JSON and every photo row: generous, but bounded.</summary>
    private const long MaxManifestBytes = 128L * 1024 * 1024;

    private static readonly MinDataRate MinBodyRate = new(bytesPerSecond: 16 * 1024, gracePeriod: TimeSpan.FromSeconds(30));

    /// <summary>Checks the package against this server and opens the import. Always 200 with the report (blockers inside).</summary>
    [HttpPost]
    [RequestSizeLimit(MaxManifestBytes)]
    public Task<IActionResult> Begin(CancellationToken ct) =>
        RunAsync(async () =>
        {
            var manifest = await JsonSerializer.DeserializeAsync<CapturePackageManifest>(Request.Body, CapturePackageFiles.Json, ct)
                           ?? throw new JsonException("The body is empty.");
            return Ok(await Packages.BeginImportAsync(manifest, ct));
        });

    /// <summary>One file of the package as the raw body. Skipped (not read) when it is already staged or in the store.</summary>
    [HttpPut("{importId:guid}/files/{name}")]
    [DisableRequestSizeLimit]
    public Task<IActionResult> PutFile(Guid importId, string name, CancellationToken ct) =>
        RunAsync(async () =>
        {
            // Per-request rates are HTTP/1.x only (Kestrel throws for HTTP/2; Caddy proxies over HTTP/1.1).
            if (HttpProtocol.IsHttp11(Request.Protocol) && HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
            {
                rate.MinDataRate = MinBodyRate;
            }

            return Ok(await Packages.PutFileAsync(importId, name, Request.Body, ct));
        });

    /// <summary>Inserts the rows and queues the capture. 200 when committed (or already imported), else 409 with the report.</summary>
    [HttpPost("{importId:guid}/commit")]
    public Task<IActionResult> Commit(Guid importId, CancellationToken ct) =>
        RunAsync(async () =>
        {
            var report = await Packages.CommitImportAsync(importId, ct);
            return report.Committed || report.AlreadyImported ? Ok(report) : Conflict(report);
        });

    /// <summary>Drops an open import and its uploaded files.</summary>
    [HttpDelete("{importId:guid}")]
    public Task<IActionResult> Abort(Guid importId, CancellationToken ct) =>
        RunAsync(async () =>
        {
            await Packages.AbortImportAsync(importId, ct);
            return NoContent();
        });
}
