// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>The recognition an enrichment run did before its gate slot (queued jobs): the outline batch and the marker pass.</summary>
/// <param name="Outlines">The outlines, or null when the outline pass does not run.</param>
/// <param name="Markers">The marker pass, or null when it did not run.</param>
internal sealed record EnrichmentPrefetch(HoldOutlineBatchResult? Outlines, MarkerPass? Markers);
