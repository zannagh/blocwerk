// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.Replay;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The export side: a finished capture's package manifest (rows, file list with sizes and SHA-256) and its files, streamed
/// from disk with range support. Nothing is written.
/// </summary>
[Route("api/v1/admin/captures/{captureId:guid}/package")]
public sealed class CapturePackagesController(ICapturePackageService packages, ILogger<CapturePackagesController> logger)
    : CapturePackageApiController(packages, logger)
{
    /// <summary>The manifest. 409 with the reason when the capture cannot be replayed without training.</summary>
    [HttpGet]
    public Task<IActionResult> Manifest(Guid captureId, CancellationToken ct) =>
        RunAsync(async () =>
        {
            var manifest = await Packages.ExportAsync(captureId, ct);
            return Content(JsonSerializer.Serialize(manifest, CapturePackageFiles.Json), "application/json");
        });

    /// <summary>One file of the package (by its stored name), or 404.</summary>
    [HttpGet("files/{name}")]
    public Task<IActionResult> File(Guid captureId, string name, CancellationToken ct) =>
        RunAsync(async () => await Packages.ExportFilePathAsync(captureId, name, ct) is { } path
            ? PhysicalFile(path, "application/octet-stream", enableRangeProcessing: true)
            : NotFound(new ApiErrorResponse("That file is not part of the capture's package, or it is gone.")));
}
