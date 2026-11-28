// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The setter flow for the companion app: create a boulder (or a variant of one) and revise it, with the offline web
/// client's bodies (<see cref="CreateBoulderRequest"/>, <see cref="ReviseBoulderRequest"/>) and the same service calls
/// (<see cref="IBoulderService.CreateBoulderAsync"/>, <see cref="IBoulderService.ReviseBoulderAsync"/>). So the rules
/// are the web's: any member may set, the creator, a setter of the boulder or a wall admin may revise, a boulder sent by
/// others keeps its holds, holds must be on the wall and confirmed, a variant stays on its parent's wall. The save writes
/// the feed rows other devices update from.
/// </summary>
/// <remarks>
/// A linked device key only, limited per key, never cached. A wall the owner is not a member of, an unknown boulder and
/// a boulder of another wall answer 404. A <c>clientRequestId</c> makes a create or a revise happen once: a retry gets
/// the boulder as it is, never a second boulder or an old revision applied over a newer one. The key is required.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}/boulders")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[DeviceKeyWriteRateLimit]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class DeviceBouldersController : ControllerBase
{
    /// <summary>The receipt kind of a create.</summary>
    public const string CreateKind = "boulder";

    /// <summary>The receipt kind of a revise.</summary>
    public const string ReviseKind = "boulder.revise";

    private readonly IBoulderService boulders;
    private readonly ICurrentUserService currentUser;
    private readonly DeviceWriteReceiptService receipts;

    public DeviceBouldersController(IBoulderService boulders, ICurrentUserService currentUser, DeviceWriteReceiptService receipts)
    {
        this.boulders = boulders;
        this.currentUser = currentUser;
        this.receipts = receipts;
    }

    /// <summary>Creates a boulder on the wall; with <c>variantOfBoulderId</c>, a variant of that boulder.</summary>
    [HttpPost]
    public Task<IActionResult> Create(Guid wallId, [FromBody] CreateBoulderRequest request)
    {
        if (DeviceBoulderRequestCheck.CreateError(request, wallId) is { } error)
        {
            return Task.FromResult<IActionResult>(OfflineActionRunner.Permanent(error));
        }

        return RunAsync(request.ClientRequestId, CreateKind, wallId, () => boulders.CreateBoulderAsync(
            wallId,
            request.Name,
            request.Grade,
            request.ToInputs(),
            request.IsDraft,
            request.KickboardFootholdsOn,
            request.HandsFollowFeet,
            request.FootColorOnly,
            request.Id == Guid.Empty ? null : request.Id,
            request.NoMatch,
            request.Setters,
            request.CustomRule,
            (BoulderStyle)request.Styles,
            request.VariantOfBoulderId));
    }

    /// <summary>
    /// Revises the boulder. Every field of <see cref="DeviceReviseBoulderRequest"/> is optional: one left out keeps the
    /// boulder's current value, so the call is mapped onto the boulder as it is before the service runs.
    /// </summary>
    [HttpPut("{boulderId:guid}")]
    public async Task<IActionResult> Revise(Guid wallId, Guid boulderId, [FromBody] DeviceReviseBoulderRequest request)
    {
        if (DeviceBoulderRequestCheck.ReviseError(request) is { } error)
        {
            return OfflineActionRunner.Permanent(error);
        }

        Boulder? current;
        try
        {
            current = await boulders.GetBoulderAsync(boulderId);
        }
        catch (UnauthorizedAccessException)
        {
            return DeviceWriteAnswers.Refused(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in to sync.");
        }

        if (current is null || current.WallId != wallId)
        {
            return DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Boulder not found");
        }

        return await RunAsync(request.ClientRequestId, ReviseKind, boulderId, () => boulders.ReviseBoulderAsync(
            boulderId,
            HoldsFor(request.Holds, current),
            request.Name,
            request.Grade,
            request.KickboardFootholdsOn,
            request.HandsFollowFeet,
            request.FootColorOnly,
            request.NoMatch ?? current.NoMatch,
            request.Setters,
            request.CustomRule,
            request.Styles is { } styles ? (BoulderStyle)styles : null,
            BoulderHoldTypeChange.FromNames(request.HoldTypes)));
    }

    // The holds to save: the ones sent, else none for a live boulder (a metadata-only save keeps them and its review
    // state), else a draft's own holds, which the service needs on every draft save.
    private static List<BoulderHoldInput>? HoldsFor(List<BoulderHoldDto>? sent, Boulder current) =>
        sent is not null ? sent.Select(h => h.ToInput()).ToList()
        : current.IsDraft ? current.BoulderHolds.Select(h => new BoulderHoldInput(h.HoldId, h.Type, h.Usage)).ToList()
        : null;

    // Takes the replay key, runs the write, completes or releases the key, and maps the service's refusals.
    private async Task<IActionResult> RunAsync(Guid? clientRequestId, string kind, Guid scopeId, Func<Task<Boulder>> write)
    {
        Guid userId;
        try
        {
            userId = (await currentUser.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return DeviceWriteAnswers.Refused(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in to sync.");
        }

        var claim = await receipts.ClaimAsync(userId, clientRequestId, kind, scopeId, HttpContext.RequestAborted);
        if (claim.State == DeviceWriteClaimState.Replay)
        {
            return await ReplayAsync(clientRequestId, claim.SubjectId!.Value);
        }

        if (DeviceWriteAnswers.Refusal(claim) is { } refused)
        {
            return refused;
        }

        try
        {
            var boulder = await write();
            await receipts.CompleteAsync(userId, clientRequestId, boulder.Id, HttpContext.RequestAborted);
            return OfflineActionRunner.Applied(clientRequestId, OfflineBouldersController.Canonical(boulder));
        }
        catch (Exception ex)
        {
            await receipts.ReleaseAsync(userId, clientRequestId);
            return Refusal(ex);
        }
    }

    // The boulder as it is now: a retry never re-applies an old snapshot over a newer revision.
    private async Task<IActionResult> ReplayAsync(Guid? clientRequestId, Guid boulderId) =>
        await boulders.GetBoulderAsync(boulderId) is { } boulder
            ? OfflineActionRunner.Applied(clientRequestId, OfflineBouldersController.Canonical(boulder))
            : DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Boulder not found");

    private static IActionResult Refusal(Exception ex) => ex switch
    {
        UnauthorizedAccessException => DeviceWriteAnswers.Refused(StatusCodes.Status401Unauthorized, "Your session has expired. Sign in to sync."),
        ArgumentOutOfRangeException => OfflineActionRunner.Permanent(ex.Message),
        InvalidOperationException { Message: "Only wall members can do this" } =>
            DeviceWriteAnswers.Refused(StatusCodes.Status404NotFound, "Wall not found"),
        InvalidOperationException guard when OfflineBouldersController.PermanentGuards.TryGetValue(guard.Message, out var status) =>
            OfflineActionRunner.Error(status, guard.Message, permanent: true),
        UserFacingException user => OfflineActionRunner.Permanent(user.Message),
        _ => Unexpected(ex),
    };

    private static IActionResult Unexpected(Exception ex)
    {
        Log.Error(ex, "Device boulder write failed");
        return OfflineActionRunner.Error(StatusCodes.Status500InternalServerError, "The boulder could not be saved. It stays queued.", permanent: false);
    }
}
