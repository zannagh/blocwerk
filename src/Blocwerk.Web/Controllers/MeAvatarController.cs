// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Changing and removing the caller's avatar from the companion app, through the profile page's
/// <see cref="ICurrentUserService.SetAvatarAsync"/>: the same type and size limits (JPEG, PNG or WebP, at most 30 MB) and
/// the same pipeline (scaled to at most 512 px and stored as WebP). Reading it stays <c>GET /api/v1/me/avatar</c>.
/// </summary>
/// <remarks>A linked device key only, on the per-key write limit, never cached.</remarks>
[ApiController]
[Route("api/v1/me/avatar")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeyWriteRateLimit]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class MeAvatarController : ControllerBase
{
    /// <summary>The largest upload the profile page streams in; the service refuses anything larger.</summary>
    public const long MaxUploadBytes = 30L * 1024 * 1024;

    private readonly ICurrentUserService currentUser;

    public MeAvatarController(ICurrentUserService currentUser)
    {
        this.currentUser = currentUser;
    }

    /// <summary>Replaces the avatar with the request body; the body's Content-Type names the format.</summary>
    [HttpPut]
    [RequestSizeLimit(MaxUploadBytes + 1)]
    public async Task<IActionResult> Put(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            return BadRequest(new ApiErrorResponse("The body must be the image."));
        }

        var contentType = Request.ContentType?.Split(';')[0].Trim();
        return await SetAsync(buffer.ToArray(), contentType);
    }

    /// <summary>Removes the avatar.</summary>
    [HttpDelete]
    public Task<IActionResult> Delete() => SetAsync(null, null);

    private async Task<IActionResult> SetAsync(byte[]? image, string? contentType)
    {
        try
        {
            await currentUser.SetAvatarAsync(image, contentType);
            currentUser.InvalidateCache();
            return Ok(MeProfileResponse.From(await currentUser.GetCurrentUserAsync()));
        }
        catch (InvalidOperationException ex)
        {
            // Too large, not JPEG/PNG/WebP, or not readable: the service's own messages.
            return BadRequest(new ApiErrorResponse(ex.Message));
        }
    }
}
