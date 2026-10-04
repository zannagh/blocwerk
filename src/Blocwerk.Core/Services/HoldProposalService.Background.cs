// <copyright file="HoldProposalService.Background.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Services;

/// <summary>The background-job side of the search (<see cref="HoldSearchJobs"/>).</summary>
public sealed partial class HoldProposalService
{
    /// <inheritdoc />
    public Task EnsureCanFindAsync(Guid wallId, CancellationToken ct = default) => EnsureAdminAsync(wallId, ct);

    /// <inheritdoc />
    public async Task<HoldProposalRunResult> FindInBackgroundAsync(Guid wallId, IProgress<HoldSearchProgress>? progress, CancellationToken ct = default)
    {
        // Another wall's search may hold the gate: this one waits its turn (it shows as queued).
        await Gate.WaitAsync(ct);
        try
        {
            return await RunAsync(wallId, ct, progress)
                ?? throw new UserFacingException("This wall has no active 3D model with capture photos to search.");
        }
        finally
        {
            Gate.Release();
        }
    }
}
