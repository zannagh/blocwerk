// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Data;

/// <summary>Timeline changes saved inside one transaction, held until it commits (<see cref="CaptureTimelineMerge"/>).</summary>
/// <param name="TransactionId">The EF transaction they were saved in.</param>
/// <param name="Changes">The changes.</param>
internal sealed record CaptureTimelineHeld(Guid TransactionId, List<CaptureTimelineChange> Changes);
