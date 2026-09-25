// <copyright file="CountingCoverageService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;

namespace Blocwerk.Core.Tests;

/// <summary>Counts the pipeline computations of a real coverage service, or throws instead.</summary>
internal sealed class CountingCoverageService(ICaptureCoverageService inner) : ICaptureCoverageService
{
    public int Computations { get; private set; }

    public bool Throws { get; init; }

    public Task<CaptureCoverageReport?> ComputeFromPipelineAsync(Guid captureId, CancellationToken ct = default)
    {
        Computations++;
        return Throws ? throw new InvalidOperationException("boom") : inner.ComputeFromPipelineAsync(captureId, ct);
    }

    public Task<CaptureCoverageLookup> GetAsync(Guid wallId, Guid captureId, CancellationToken ct = default) =>
        inner.GetAsync(wallId, captureId, ct);
}
