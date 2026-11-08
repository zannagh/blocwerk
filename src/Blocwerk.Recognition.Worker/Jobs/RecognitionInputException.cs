// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>The job's input cannot be served by any try (bad envelope, photo too large, unknown model): fails for good.</summary>
public sealed class RecognitionInputException(string message, string? failureKind = null) : Exception(message)
{
    public string? FailureKind { get; } = failureKind;
}
