// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>An upload stopped while it streamed (a gzip bomb, the disk running full); carries the answer to give.</summary>
public sealed class RunnerUploadRefusedException(RunnerJobOutcome outcome, string message) : IOException(message)
{
    public RunnerJobOutcome Outcome { get; } = outcome;
}
