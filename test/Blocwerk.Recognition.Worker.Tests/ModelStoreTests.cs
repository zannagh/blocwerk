// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Recognition.Worker.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>Models by hash: the local mount first, else one checked download into the cache.</summary>
public sealed class ModelStoreTests : IDisposable
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];

    private readonly string root = Path.Combine(Path.GetTempPath(), $"blocwerk-model-store-{Guid.NewGuid():N}");
    private readonly FakeRunnerServer server = new();

    [Fact]
    public async Task ALocalModelIsFoundByItsHashWithoutTheServer()
    {
        Directory.CreateDirectory(Path.Combine(root, "models"));
        var path = Path.Combine(root, "models", "climbingcrux.onnx");
        await File.WriteAllBytesAsync(path, Bytes);
        var store = Store();

        Assert.Equal(path, await store.ResolveAsync(Sha(Bytes), CancellationToken.None));
        Assert.Equal((path, Sha(Bytes)), await store.FindLocalAsync("climbingcrux.onnx", CancellationToken.None));
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task AnUnknownModelIsFetchedOnceAndCached()
    {
        server.Model = Bytes;
        var store = Store();

        var first = await store.ResolveAsync(Sha(Bytes), CancellationToken.None);
        var second = await store.ResolveAsync(Sha(Bytes), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(first));
        Assert.Single(server.Calls, c => c.Path == $"/api/runners/models/{Sha(Bytes)}");
    }

    [Fact]
    public async Task AFetchedModelWithAnotherHashIsRefused()
    {
        server.Model = [9, 9];

        await Assert.ThrowsAsync<InvalidDataException>(() => Store().ResolveAsync(Sha(Bytes), CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "cache")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private ModelStore Store()
    {
        var options = TestOptions.Create() with { ModelsDirectory = Path.Combine(root, "models"), ModelCacheDirectory = Path.Combine(root, "cache") };
        return new ModelStore(options, server.Client(options), NullLogger<ModelStore>.Instance);
    }
}
