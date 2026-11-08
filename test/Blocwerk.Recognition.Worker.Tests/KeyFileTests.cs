// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Recognition.Worker.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>Zero-config: without a key in the environment the worker reads the key file the app generates, once it exists.</summary>
public sealed class KeyFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"blocwerk-worker-key-{Guid.NewGuid():N}");

    [Fact]
    public void WithoutAKeyTheKeyFileIsUsed_AndAKeyInTheEnvironmentWins()
    {
        var fromFile = Bind(new() { ["Recognition:InstanceKeyFile"] = "/recognition/instance.key" });
        var fromEnvironment = Bind(new() { ["Recognition:InstanceKeyFile"] = "/recognition/instance.key", ["Recognition:InstanceKey"] = "k" });

        Assert.Equal((string.Empty, "/recognition/instance.key"), (fromFile.Key, fromFile.KeyFile));
        Assert.Equal(("k", null), (fromEnvironment.Key, fromEnvironment.KeyFile));
        Assert.Throws<InvalidOperationException>(() => Bind([]));
    }

    [Fact]
    public async Task TheWorkerWaitsForTheKeyFileInsteadOfExiting()
    {
        var path = Path.Combine(root, "instance.key");
        var options = TestOptions.Create();
        var health = new WorkerHealth(options, TimeProvider.System);

        var read = KeyFileWait.ReadAsync(path, options, health, NullLogger.Instance, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(read.IsCompleted);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "generated-key\n");

        Assert.Equal("generated-key", await read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(health.Live);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WorkerOptions Bind(Dictionary<string, string?> values) =>
        WorkerOptions.Bind(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
