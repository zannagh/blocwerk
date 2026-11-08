// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Recognition.Worker.Envelope;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>Runs one recognition kind (= capability) on the existing HoldDetection services.</summary>
public interface IRecognitionJobHandler
{
    string Kind { get; }

    /// <summary>Runs the job; the returned object is serialized as the kind's result document.</summary>
    Task<object> RunAsync(RecognitionEnvelope envelope, CancellationToken ct);
}
