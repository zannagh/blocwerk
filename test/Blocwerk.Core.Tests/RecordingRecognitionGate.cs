// <copyright file="RecordingRecognitionGate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Numbers its runs and tells which one (0 for none) is executing right now.</summary>
internal sealed class RecordingRecognitionGate : IImageRecognitionGate
{
    public bool Enabled { get; init; } = true;

    public List<string> Runs { get; } = [];

    public int Inside { get; private set; }

    public async Task<T> RunAsync<T>(string operation, Func<Task<T>> work, CancellationToken ct = default)
    {
        Runs.Add(operation);
        Inside = Runs.Count;
        try
        {
            return await work();
        }
        finally
        {
            Inside = 0;
        }
    }

    public Task<IDisposable> EnterAsync(string operation, CancellationToken ct = default) => throw new NotSupportedException();

    public IDisposable Enter(string operation, CancellationToken ct = default) => throw new NotSupportedException();
}
