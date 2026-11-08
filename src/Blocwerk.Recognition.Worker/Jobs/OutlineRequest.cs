// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>Contracts <c>jobs/recognition/outline-request</c>: the seeds, and <see cref="HoldOutlineBatchOptions"/> (absent: separately).</summary>
public sealed record OutlineRequest(IReadOnlyList<OutlineRequestSeed> Seeds, HoldOutlineBatchOptions? Options);
