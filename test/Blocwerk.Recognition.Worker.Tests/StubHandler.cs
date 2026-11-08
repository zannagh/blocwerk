// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Recognition.Worker.Envelope;
using Blocwerk.Recognition.Worker.Jobs;
using Blocwerk.Recognition.Worker.Runtime;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>A job handler for one kind that runs a delegate (the OpenCV natives do not load on every test machine).</summary>
internal sealed class StubHandler(string kind, Func<RecognitionEnvelope, CancellationToken, Task<object>> run) : IRecognitionJobHandler, IMemoryProbe
{
    public string Kind => kind;

    public long MemoryBytes { get; set; } = 100L * 1024 * 1024;

    public Task<object> RunAsync(RecognitionEnvelope envelope, CancellationToken ct) => run(envelope, ct);

    public long CurrentBytes() => MemoryBytes;
}
