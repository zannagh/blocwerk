// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Compute;

/// <summary>What a worker reports it takes per job (<c>GET /v1/info</c>, <c>limits</c>); a null field: it does not say.</summary>
/// <param name="MaxImages">Images per job (<c>maxPhotos</c>).</param>
/// <param name="MaxRequestBytes">Bytes per request (<c>maxRequestMb</c>).</param>
public sealed record ComputeLimits(int? MaxImages, long? MaxRequestBytes);
