// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Protocol;

/// <summary>
/// <c>POST …/fail</c> (contracts <c>runner/failure</c>). <c>FailureKind</c> <c>"resource"</c>: the job outgrew this runner's
/// memory (protocol §1.4), so the server leaves it to a runner with a bigger budget.
/// </summary>
public sealed record RunnerFailure(string Reason, bool Retryable, bool Shutdown = false, string? FailureKind = null)
{
    public const string Resource = "resource";
}
