// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The web boulder page's actions for the companion app, through <see cref="IBoulderService"/>: publish a draft, archive
/// and restore a historic boulder, the grade proposal (read, propose, accept, reject) and the variant links (Mark
/// Variant: the parent, or the list of variants). The service's one authority rule applies as on the web: the creator,
/// a setter or a wall admin publishes, archives, resolves proposals and links variants; anyone but the creator proposes.
/// </summary>
/// <remarks>
/// A linked device key only, never cached. A wall the caller is not a member of, an unknown boulder and a boulder of
/// another wall answer 404. Refusals are <c>{ message }</c>. Publish, archive and the links are absolute, so a retry
/// sets the same state again. A proposal may carry a <c>clientRequestId</c>: a queued proposal retried after a newer one
/// would otherwise put its older grade back. An accept names the grade the caller saw, so it never applies a proposal
/// that changed meanwhile.
/// </remarks>
[ApiController]
[Route("api/v1/walls/{wallId:guid}/boulders/{boulderId:guid}")]
[Authorize(Policy = BlocwerkPolicies.DeviceApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[DeviceKeyOnly]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
public sealed class DeviceBoulderActionsController : ControllerBase
{
    /// <summary>The most variants one call may link.</summary>
    public const int MaxVariants = 100;

    /// <summary>The receipt kind of a grade proposal.</summary>
    public const string ProposalKind = "grade.proposal";

    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly ICurrentUserService currentUser;
    private readonly IBoulderService boulders;
    private readonly DeviceWriteReceiptService receipts;

    public DeviceBoulderActionsController(
        IDbContextFactory<BlocwerkDbContext> contexts,
        ICurrentUserService currentUser,
        IBoulderService boulders,
        DeviceWriteReceiptService receipts)
    {
        this.contexts = contexts;
        this.currentUser = currentUser;
        this.boulders = boulders;
        this.receipts = receipts;
    }

    /// <summary>Publishes a draft; publishing a published boulder is a no-op.</summary>
    [HttpPost("publish")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> Publish(Guid wallId, Guid boulderId, CancellationToken ct) =>
        RunAsync(wallId, boulderId, ct, async _ => Ok(OfflineBouldersController.Canonical(await boulders.PublishBoulderAsync(boulderId))));

    /// <summary>Moves a historic boulder into the archive.</summary>
    [HttpPost("archive")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> Archive(Guid wallId, Guid boulderId, CancellationToken ct) =>
        RunAsync(wallId, boulderId, ct, async _ =>
        {
            await boulders.ArchiveBoulderAsync(boulderId);
            return Ok(new { boulderId, isArchived = true });
        });

    /// <summary>Restores a boulder from the archive.</summary>
    [HttpPost("unarchive")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> Unarchive(Guid wallId, Guid boulderId, CancellationToken ct) =>
        RunAsync(wallId, boulderId, ct, async _ =>
        {
            await boulders.UnarchiveBoulderAsync(boulderId);
            return Ok(new { boulderId, isArchived = false });
        });

    /// <summary>The open grade proposal, and whether the caller may propose or resolve one.</summary>
    [HttpGet("grade-proposal")]
    [DeviceKeySyncRateLimit]
    public Task<IActionResult> GetProposal(Guid wallId, Guid boulderId, CancellationToken ct) =>
        RunAsync(wallId, boulderId, ct, async ctx => Ok(await StateAsync(ctx)));

    /// <summary>Proposes a different grade; a second proposal replaces the open one.</summary>
    [HttpPost("grade-proposal")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> Propose(Guid wallId, Guid boulderId, [FromBody] DeviceBoulderActionRequests request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Grade) || GradeScale.IsFont(request.Grade.Trim()) is null)
        {
            return Task.FromResult<IActionResult>(DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, "grade must be a Font or V grade."));
        }

        if (request.ClientRequestId == Guid.Empty)
        {
            return Task.FromResult<IActionResult>(DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, ClientRequestIds.EmptyMessage));
        }

        return RunAsync(wallId, boulderId, ct, async ctx =>
        {
            var claim = await receipts.ClaimAsync(ctx.Caller.UserId, request.ClientRequestId, ProposalKind, boulderId, ct);
            if (claim.State == DeviceWriteClaimState.Replay)
            {
                return Ok(await StateAsync(ctx));
            }

            if (claim.State != DeviceWriteClaimState.Fresh)
            {
                return DeviceWallRefusal.Refused(
                    claim.State == DeviceWriteClaimState.InFlight ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                    claim.State == DeviceWriteClaimState.InFlight ? "The same proposal is still being saved." : "clientRequestId was already used for another write.");
            }

            try
            {
                var proposal = await boulders.ProposeGradeAsync(boulderId, request.Grade.Trim());
                await receipts.CompleteAsync(ctx.Caller.UserId, request.ClientRequestId, proposal.Id, ct);
            }
            catch
            {
                await receipts.ReleaseAsync(ctx.Caller.UserId, request.ClientRequestId);
                throw;
            }

            return Ok(await StateAsync(ctx));
        });
    }

    /// <summary>
    /// Accepts the open proposal: the boulder takes its grade. <c>expectedGrade</c> is the grade the caller saw; a proposal
    /// that changed meanwhile is a 409.
    /// </summary>
    [HttpPost("grade-proposal/{proposalId:guid}/accept")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> Accept(Guid wallId, Guid boulderId, Guid proposalId, [FromBody] DeviceBoulderActionRequests request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedGrade))
        {
            return Task.FromResult<IActionResult>(DeviceWallRefusal.Refused(StatusCodes.Status400BadRequest, "expectedGrade is required."));
        }

        return ResolveAsync(wallId, boulderId, proposalId, ct, () => boulders.AcceptGradeProposalAsync(proposalId, request.ExpectedGrade.Trim()));
    }

    /// <summary>Rejects the open proposal.</summary>
    [HttpPost("grade-proposal/{proposalId:guid}/reject")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> Reject(Guid wallId, Guid boulderId, Guid proposalId, CancellationToken ct) =>
        ResolveAsync(wallId, boulderId, proposalId, ct, () => boulders.RejectGradeProposalAsync(proposalId));

    /// <summary>Makes the boulder a variant of <c>parentId</c>, or no variant with null.</summary>
    [HttpPut("parent")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> SetParent(Guid wallId, Guid boulderId, [FromBody] DeviceBoulderActionRequests request, CancellationToken ct) =>
        RunAsync(wallId, boulderId, ct, async _ =>
        {
            await boulders.SetParentAsync(boulderId, request.ParentId, ct);
            return Ok(await LinksAsync(boulderId, ct));
        });

    /// <summary>Sets the boulders that are variants of this one (the list replaces the stored one).</summary>
    [HttpPut("variants")]
    [DeviceKeyWriteRateLimit]
    public Task<IActionResult> SetVariants(Guid wallId, Guid boulderId, [FromBody] DeviceBoulderActionRequests request, CancellationToken ct)
    {
        var ids = request.VariantIds ?? [];
        if (ids.Count > MaxVariants || ids.Distinct().Count() != ids.Count || ids.Contains(Guid.Empty))
        {
            return Task.FromResult<IActionResult>(DeviceWallRefusal.Refused(
                StatusCodes.Status400BadRequest, $"variantIds must be at most {MaxVariants} distinct boulder ids."));
        }

        return RunAsync(wallId, boulderId, ct, async _ =>
        {
            await boulders.SetVariantsAsync(boulderId, ids, ct);
            return Ok(await LinksAsync(boulderId, ct));
        });
    }

    private Task<IActionResult> ResolveAsync(Guid wallId, Guid boulderId, Guid proposalId, CancellationToken ct, Func<Task> resolve) =>
        RunAsync(wallId, boulderId, ct, async ctx =>
        {
            if ((await boulders.GetActiveProposalAsync(boulderId))?.Id != proposalId)
            {
                return DeviceWallRefusal.Refused(StatusCodes.Status404NotFound, "Proposal not found");
            }

            await resolve();
            return Ok(await StateAsync(ctx));
        });

    // The links as stored after the save: the boulder's parent, and the boulders that are its variants.
    private async Task<DeviceBoulderLinks> LinksAsync(Guid boulderId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var parentId = await db.Boulders.AsNoTracking().Where(b => b.Id == boulderId).Select(b => b.ParentBoulderId).FirstOrDefaultAsync(ct);
        var variants = await db.Boulders.AsNoTracking().Where(b => b.ParentBoulderId == boulderId).Select(b => b.Id).ToListAsync(ct);
        return new DeviceBoulderLinks(boulderId, parentId, variants.Order().ToList());
    }

    private async Task<DeviceGradeProposalState> StateAsync(DeviceBoulderContext ctx)
    {
        var open = await boulders.GetActiveProposalAsync(ctx.Boulder.Id);
        var proposal = open is null
            ? null
            : new DeviceGradeProposal(open.Id, open.ProposedGrade, open.ProposedByUserId, open.ProposedBy?.Name ?? "Unknown", open.CreatedAt);
        var mayManage = ctx.Boulder.CreatedByUserId == ctx.Caller.UserId
                        || ctx.Boulder.Setters.Any(s => s.UserId == ctx.Caller.UserId)
                        || ctx.Caller.IsAdmin;
        return new DeviceGradeProposalState(proposal, ctx.Boulder.CreatedByUserId != ctx.Caller.UserId, mayManage);
    }

    // The caller is a member, the boulder is on this wall: run the action, and map the service's refusals.
    private async Task<IActionResult> RunAsync(Guid wallId, Guid boulderId, CancellationToken ct, Func<DeviceBoulderContext, Task<IActionResult>> action)
    {
        try
        {
            var caller = await WallCaller.ReadAsync(contexts, currentUser, wallId, ct);
            if (caller is not { IsMember: true } || await boulders.GetBoulderAsync(boulderId) is not { } boulder || boulder.WallId != wallId)
            {
                return NotFound();
            }

            return await action(new DeviceBoulderContext(caller, boulder));
        }
        catch (Exception ex) when (DeviceBoulderActionRefusal.From(ex) is { } refusal)
        {
            return refusal;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Device boulder action failed for boulder {BoulderId}", boulderId);
            return DeviceWallRefusal.Refused(StatusCodes.Status500InternalServerError, "The change could not be saved.");
        }
    }
}
