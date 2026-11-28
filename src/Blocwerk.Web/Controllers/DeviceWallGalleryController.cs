// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The wall gallery for the companion app: add a photo, delete one. The upload is the camera's pipeline
/// (<see cref="WallImageIntake"/>: at most 20 MB of JPEG, PNG or WebP, metadata stripped), the rule is the web gallery's
/// (wall admins only, <see cref="IWallImageService.EnsureCanUploadAsync"/> and <see cref="IWallImageService.DeleteImageAsync"/>).
/// The app reads the gallery through the wall's <c>gallery</c> document; the new row reaches it through the feed.
/// </summary>
/// <remarks>
/// A linked device key only, limited per key, never cached. A wall the owner is not a member of answers 404, a member
/// who does not administer it 403. An upload with a <c>clientRequestId</c> (query string, so it is known before the
/// body is read) is done once: a retry returns the stored photo.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}/gallery")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeyWriteRateLimit]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class DeviceWallGalleryController : ControllerBase
{
    /// <summary>The receipt kind of a gallery upload.</summary>
    public const string ReceiptKind = "gallery";

    private readonly IWallImageService images;
    private readonly IWallImageStorage storage;
    private readonly ICurrentUserService currentUser;
    private readonly DeviceWriteReceiptService receipts;

    public DeviceWallGalleryController(
        IWallImageService images,
        IWallImageStorage storage,
        ICurrentUserService currentUser,
        DeviceWriteReceiptService receipts)
    {
        this.images = images;
        this.storage = storage;
        this.currentUser = currentUser;
        this.receipts = receipts;
    }

    /// <summary>Adds one photo: multipart with a <c>file</c> part, or the raw image as the body.</summary>
    [HttpPost]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Upload(Guid wallId, [FromQuery] Guid? clientRequestId, CancellationToken ct)
    {
        if (clientRequestId == Guid.Empty)
        {
            return OfflineActionRunner.Permanent(ClientRequestIds.EmptyMessage);
        }

        var (user, denied) = await AdminOfAsync(wallId, ct);
        if (user is null)
        {
            return denied!;
        }

        var claim = await receipts.ClaimAsync(user.Id, clientRequestId, ReceiptKind, wallId, ct);

        if (claim.State == DeviceWriteClaimState.Replay)
        {
            var stored = await images.GetImageAsync(claim.SubjectId!.Value, ct);
            return OfflineActionRunner.Applied(clientRequestId, new { imageId = claim.SubjectId, capturedAt = stored?.CapturedAt });
        }

        if (DeviceWriteAnswers.Refusal(claim) is { } refused)
        {
            return refused;
        }

        return await StoreAsync(wallId, user.Id, clientRequestId, ct);
    }

    /// <summary>Deletes one uploaded photo of the wall; the wall's own photo and retired photos are not deletable.</summary>
    [HttpDelete("{imageId:guid}")]
    public async Task<IActionResult> Delete(Guid wallId, Guid imageId, [FromQuery] Guid? clientRequestId, CancellationToken ct)
    {
        var (user, denied) = await AdminOfAsync(wallId, ct);
        if (user is null)
        {
            return denied!;
        }

        var image = await images.GetImageAsync(imageId, ct);
        if (image is null || image.WallId != wallId)
        {
            return DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Image not found.");
        }

        try
        {
            await images.DeleteImageAsync(imageId, user.Id, ct);
        }
        catch (InvalidOperationException)
        {
            // Deleted meanwhile by someone else.
            return DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Image not found.");
        }

        return OfflineActionRunner.Applied(clientRequestId, new { imageId });
    }

    // The caller when they administer the wall; otherwise the refusal to answer.
    private async Task<(User? User, IActionResult? Refusal)> AdminOfAsync(Guid wallId, CancellationToken ct)
    {
        User user;
        try
        {
            user = await currentUser.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            return (null, DeviceWriteAnswers.Refused(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in to sync."));
        }

        try
        {
            await images.EnsureCanUploadAsync(wallId, user.Id, ct);
            return (user, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, DeviceWriteAnswers.Refused(StatusCodes.Status403Forbidden, "Only the wall's admins can change its gallery."));
        }
        catch (InvalidOperationException)
        {
            return (null, DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Wall not found"));
        }
    }

    private async Task<IActionResult> StoreAsync(Guid wallId, Guid userId, Guid? clientRequestId, CancellationToken ct)
    {
        // A hard deadline, so a receipt is never taken over while its upload may still be running.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DeviceWriteReceiptService.RequestTimeout);
        var upload = await WallImageIntake.ReadAsync(Request, storage, deadline.Token);
        var commit = upload.IsSuccess
            ? await WallImageIntake.CommitAsync(storage, images, wallId, upload, deadline.Token)
            : WallImageCommit.Failed(upload.ErrorStatus!.Value, upload.ErrorMessage!);
        if (commit.Image is not { } image)
        {
            await receipts.ReleaseAsync(userId, clientRequestId);
            return DeviceWriteAnswers.Refused(commit.ErrorStatus!.Value, commit.ErrorMessage!);
        }

        await receipts.CompleteAsync(userId, clientRequestId, image.Id, ct);
        return OfflineActionRunner.Applied(clientRequestId, new { imageId = image.Id, capturedAt = (DateTimeOffset?)image.CapturedAt });
    }
}
