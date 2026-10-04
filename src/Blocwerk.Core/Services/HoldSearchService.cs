// <copyright file="HoldSearchService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldSearchService"/>: checks the caller (wall admin, not a kiosk) in the request's scope, then runs the
/// search in a scope of its own so it outlives the circuit or request that started it.
/// </summary>
public sealed class HoldSearchService(IHoldProposalService proposals, HoldSearchJobs jobs, IServiceScopeFactory scopes) : IHoldSearchService
{
    /// <inheritdoc />
    public async Task<HoldSearchStatus> StartAsync(Guid wallId, CancellationToken ct = default)
    {
        await proposals.EnsureCanFindAsync(wallId, ct);
        return jobs.TryStart(wallId, (progress, token) => RunAsync(wallId, progress, token), out var status)
            ? status
            : throw new UserFacingException("A hold search is already running for this wall. Its progress is shown here.");
    }

    /// <inheritdoc />
    public async Task<HoldSearchStatus?> GetAsync(Guid wallId, CancellationToken ct = default)
    {
        await proposals.EnsureCanFindAsync(wallId, ct);
        return jobs.Get(wallId);
    }

    private async Task<HoldProposalRunResult> RunAsync(Guid wallId, IProgress<HoldSearchProgress> progress, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IHoldProposalService>().FindInBackgroundAsync(wallId, progress, ct);
    }
}
