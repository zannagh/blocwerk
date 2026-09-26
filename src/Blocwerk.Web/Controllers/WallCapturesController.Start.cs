// <copyright file="WallCapturesController.Start.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>Plan, declarations and start of a draft, plus the shared plumbing of the capture routes.</summary>
public sealed partial class WallCapturesController
{
    /// <summary>
    /// Uses this marker plan JSON (the marker planner's marker-plan.json) for the draft. 422 with the reasons when it
    /// is not a usable plan. Optional: without it the wall's saved plan (or the legacy ids) is used.
    /// </summary>
    [HttpPut("{captureId:guid}/plan")]
    public Task<IActionResult> AttachPlan(Guid wallId, Guid captureId, [FromBody] JsonElement plan) =>
        ForCaptureAsync(wallId, captureId, async () =>
        {
            var result = await captures.AttachPlanAsync(captureId, plan.GetRawText());
            return result.Accepted ? Ok(result) : UnprocessableEntity(result);
        });

    /// <summary>The declarations the server suggests for the photos so far, with the warnings they raise.</summary>
    [HttpGet("{captureId:guid}/declarations")]
    public Task<IActionResult> Declarations(Guid wallId, Guid captureId) =>
        ForCaptureAsync(wallId, captureId, async () =>
        {
            var suggested = await captures.SuggestDeclarationsAsync(captureId);
            return Ok(new CaptureDeclarationsResponse(suggested, CaptureDeclarationRules.MergeWarnings(suggested)));
        });

    /// <summary>
    /// Starts the draft with the given (or the suggested) declarations and quality. 202 with warnings when it is
    /// queued, 422 with the problems when it cannot start (nothing changes then).
    /// </summary>
    [HttpPost("{captureId:guid}/start")]
    public Task<IActionResult> Start(Guid wallId, Guid captureId, [FromBody] CaptureStartRequest? request) =>
        ForCaptureAsync(wallId, captureId, async () =>
        {
            var declarations = await DeclarationsFor(captureId, request);
            var problems = await captures.StartAsync(
                captureId,
                declarations,
                request?.Notes,
                request?.Quality ?? SplatQuality.High,
                request?.GeometryMode ?? CaptureGeometryOverride.Auto);
            return problems.Count > 0
                ? UnprocessableEntity(new CaptureStartProblems(problems))
                : Accepted(new CaptureStartResponse(captureId, CaptureDeclarationRules.MergeWarnings(declarations)));
        });

    /// <summary>The route's wall guard, then 404 unless the capture belongs to that wall, then the action.</summary>
    private Task<IActionResult> ForCaptureAsync(Guid wallId, Guid captureId, Func<Task<IActionResult>> action) =>
        RunAsync(wallId, async () =>
        {
            WallCaptureSummary? capture;
            try
            {
                capture = await captures.GetCaptureAsync(captureId);
            }
            catch (UserFacingException)
            {
                capture = null;
            }

            return capture?.WallId == wallId ? await action() : NotFound(new ApiErrorResponse("There is no such capture on this wall."));
        });

    /// <summary>What the caller sent, the server's suggestion for whatever it left out.</summary>
    private async Task<CaptureDeclarations> DeclarationsFor(Guid captureId, CaptureStartRequest? request)
    {
        if (request?.Segments is { } segments && request.LevelPairs is { } pairs)
        {
            return new CaptureDeclarations(segments, pairs);
        }

        var suggested = await captures.SuggestDeclarationsAsync(captureId);
        return new CaptureDeclarations(request?.Segments ?? suggested.Segments, request?.LevelPairs ?? suggested.LevelPairs);
    }

    /// <summary>One photo into the draft; a refusal becomes the item's error (a gate refusal still ends the request).</summary>
    private async Task<CapturePhotoUploadItem> AddPhotoAsync(Guid captureId, string? name, byte[]? bytes, CancellationToken ct)
    {
        if (bytes is null)
        {
            return new CapturePhotoUploadItem(name, null, $"{name ?? "The photo"} is larger than {options.MaxPhotoBytes / (1024 * 1024)} MB.");
        }

        try
        {
            return new CapturePhotoUploadItem(name, await captures.AddPhotoAsync(captureId, name, bytes, ct), null);
        }
        catch (UserFacingException ex)
        {
            return new CapturePhotoUploadItem(name, null, ex.Message);
        }
        catch (InvalidDataException)
        {
            // A corrupt image fails in the metadata stripper; one bad file must not end the upload.
            return new CapturePhotoUploadItem(name, null, $"{name ?? "The photo"} couldn't be read.");
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(ex, "Capture {CaptureId}: photo {FileName} could not be stored", captureId, name);
            return new CapturePhotoUploadItem(name, null, $"{name ?? "The photo"} could not be stored.");
        }
    }

    /// <summary>Lets a whole batch in (up to the draft's photo limit at the largest photo size); each part is capped on its own.</summary>
    private void AllowPhotoBatchBody()
    {
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = (WallCapturePipelineOptions.MaxPhotos * (options.MaxPhotoBytes + (64 * 1024))) + (1024 * 1024);
        }
    }
}
