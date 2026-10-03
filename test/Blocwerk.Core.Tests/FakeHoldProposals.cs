// <copyright file="FakeHoldProposals.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>Counts pipeline searches; the review side is not used here.</summary>
internal sealed class FakeHoldProposals : IHoldProposalService
{
    public int Proposals { get; init; }

    public bool Throws { get; init; }

    public int Calls { get; private set; }

    public Task<HoldProposalRunResult?> FindFromPipelineAsync(Guid wallId, CancellationToken ct = default)
    {
        Calls++;
        return Throws
            ? throw new InvalidOperationException("boom")
            : Task.FromResult<HoldProposalRunResult?>(new HoldProposalRunResult(53, 900, 40, Proposals, Proposals, "test"));
    }

    public Task<HoldProposalRunResult> FindAsync(Guid wallId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<HoldProposal>> ListAsync(Guid wallId, HoldProposalStatus status = HoldProposalStatus.Pending, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Hold> AcceptAsync(Guid wallId, Guid proposalId, string? color, HoldCategory category, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RejectAsync(Guid wallId, Guid proposalId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<byte[]?> CropAsync(Guid wallId, Guid proposalId, CancellationToken ct = default) => throw new NotSupportedException();
}
