// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Protocol;

/// <summary><c>POST /api/runners/hello</c> (contracts <c>runner/hello</c>).</summary>
public sealed record RunnerHello(string RunnerVersion, string Platform, long MemoryBudgetMb, bool Paused, IReadOnlyList<string> Capabilities);
