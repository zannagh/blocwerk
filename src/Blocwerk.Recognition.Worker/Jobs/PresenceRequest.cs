// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>Contracts <c>jobs/recognition/presence-request</c>.</summary>
public sealed record PresenceRequest(IReadOnlyList<PresenceQuery> Queries);
