// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Protocol;

/// <summary>A claimed job (contracts <c>runner/claim</c>), the fields this worker reads.</summary>
public sealed record RunnerClaim(
    Guid JobId,
    int LeaseSeconds,
    long BundleBytes,
    string BundleSha256,
    string Kind = "splat",
    int? RequiredMemoryMb = null,
    bool Reattached = false);
