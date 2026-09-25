// <copyright file="IHoldProposalService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// "Find holds from all photos": detects holds in every capture photo of the active model, lifts them onto the
/// facets and volumes through the solved cameras, clusters them across photos and proposes the ones that match no
/// existing hold (<see cref="HoldProposal"/>). Proposals never become holds by themselves: a wall admin accepts
/// (a hold is created on the proposal's panel through the normal hold-creation path) or rejects (remembered).
/// </summary>
public interface IHoldProposalService
{
    /// <summary>Runs the search on a wall admin's request (a few minutes on the CPU for ~50 photos).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<HoldProposalRunResult> FindAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Runs the search without a user (pipeline); never throws.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome, or null when not possible or failed.</returns>
    Task<HoldProposalRunResult?> FindFromPipelineAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The wall's proposals with the given status (wall admins).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="status">The status.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The proposals, most photos first.</returns>
    Task<IReadOnlyList<HoldProposal>> ListAsync(Guid wallId, HoldProposalStatus status = HoldProposalStatus.Pending, CancellationToken ct = default);

    /// <summary>Accepts a proposal: creates the hold on its panel (wall editors, as any added hold).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="proposalId">The proposal.</param>
    /// <param name="color">The hold's colour key, or null.</param>
    /// <param name="category">Hand or foot.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The created hold.</returns>
    Task<Hold> AcceptAsync(Guid wallId, Guid proposalId, string? color, HoldCategory category, CancellationToken ct = default);

    /// <summary>Rejects a proposal (remembered: the spot is not proposed again).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="proposalId">The proposal.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RejectAsync(Guid wallId, Guid proposalId, CancellationToken ct = default);

    /// <summary>A JPEG crop of the proposal's clearest capture photo around it (wall admins).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="proposalId">The proposal.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The JPEG, or null when the photo is gone.</returns>
    Task<byte[]?> CropAsync(Guid wallId, Guid proposalId, CancellationToken ct = default);
}

/// <summary>The outcome of one proposal run.</summary>
/// <param name="Photos">Capture photos searched.</param>
/// <param name="Detections">Detections in them.</param>
/// <param name="Clusters">Holds seen by ≥ 2 photos.</param>
/// <param name="Proposals">New pending proposals (clusters matching no hold and no reviewed proposal).</param>
/// <param name="OnPanels">Of those, the ones that map into a panel photo (the rest are "3D only").</param>
/// <param name="Detector">The detector used.</param>
public sealed record HoldProposalRunResult(int Photos, int Detections, int Clusters, int Proposals, int OnPanels, string Detector);
