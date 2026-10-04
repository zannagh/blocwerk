// <copyright file="IHoldSearchService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// "Find holds from all photos" as a background job (<see cref="HoldSearchJobs"/>): wall admins start it and come back
/// to see its progress or result; the proposals it finds are stored as ever (<see cref="IHoldProposalService"/>).
/// </summary>
public interface IHoldSearchService
{
    /// <summary>Starts the search in the background (wall admins).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation of the request, not of the search.</param>
    /// <returns>The new status.</returns>
    /// <exception cref="UserFacingException">The wall already has a search running or queued.</exception>
    Task<HoldSearchStatus> StartAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The wall's latest search (wall admins), or null when none ran since the server started.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status.</returns>
    Task<HoldSearchStatus?> GetAsync(Guid wallId, CancellationToken ct = default);
}
