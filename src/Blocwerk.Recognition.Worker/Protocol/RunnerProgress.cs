// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Protocol;

/// <summary>The lease heartbeat (contracts <c>runner/progress</c>).</summary>
public sealed record RunnerProgress(string Stage, double? Fraction = null, string? Detail = null);
