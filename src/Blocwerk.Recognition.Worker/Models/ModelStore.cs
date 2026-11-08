// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Recognition.Worker.Jobs;
using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Models;

/// <summary>
/// Finds a model by SHA-256: first among the files of the read-only models directory (the pod's mount), then in the cache
/// directory, else it fetches <c>GET /api/runners/models/{sha256}</c> once into the cache (external runners). The cache keeps
/// the <see cref="CachedModels"/> most recently used models; older ones are deleted after a fetch.
/// </summary>
public sealed class ModelStore(WorkerOptions options, RunnerApiClient api, ILogger<ModelStore> logger)
{
    public const int CachedModels = 2;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> known = new(StringComparer.Ordinal);
    private bool scanned;

    /// <summary>The path of the model with this hash, fetched from the server when no local file has it.</summary>
    public async Task<string> ResolveAsync(string sha256, CancellationToken ct)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new RecognitionInputException("The request's model hash is not 64 lower-case hex digits.");
        }

        await gate.WaitAsync(ct);
        try
        {
            await ScanAsync(ct);
            if (known.TryGetValue(sha256, out var path))
            {
                if (InCache(path))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }

                return path;
            }

            Directory.CreateDirectory(options.ModelCacheDirectory);
            var target = Path.Combine(options.ModelCacheDirectory, $"{sha256}.onnx");
            var partial = target + ".part";
            logger.LogInformation("Fetching model {Sha256} from the server", sha256);
            await api.DownloadModelAsync(sha256, partial, ct);
            if (await HashAsync(partial, ct) != sha256)
            {
                File.Delete(partial);
                throw new InvalidDataException($"The downloaded model does not have the SHA-256 {sha256}.");
            }

            File.Move(partial, target, overwrite: true);
            known[sha256] = target;
            Evict(target);
            return target;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The model file <paramref name="fileName"/> in the models directory and its hash, or null when absent.</summary>
    public async Task<(string Path, string Sha256)?> FindLocalAsync(string fileName, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await ScanAsync(ct);
            var path = Path.GetFullPath(Path.Combine(options.ModelsDirectory, fileName));
            return known.FirstOrDefault(k => k.Value == path) is { Key: not null } hit ? (hit.Value, hit.Key) : null;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool InCache(string path) =>
        Path.GetDirectoryName(path) == Full(options.ModelCacheDirectory) && Full(options.ModelCacheDirectory) != Full(options.ModelsDirectory);

    private static string Full(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>Deletes the cached models beyond the <see cref="CachedModels"/> most recently used (<paramref name="keep"/> one of them).</summary>
    private void Evict(string keep)
    {
        var stale = known.Where(k => k.Value != keep && InCache(k.Value))
            .OrderByDescending(k => File.GetLastWriteTimeUtc(k.Value))
            .Skip(CachedModels - 1)
            .ToList();
        foreach (var (sha256, path) in stale)
        {
            File.Delete(path);
            known.Remove(sha256);
            logger.LogInformation("Model {Sha256} evicted from the cache", sha256);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        if (scanned)
        {
            return;
        }

        foreach (var directory in new[] { options.ModelsDirectory, options.ModelCacheDirectory }.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(directory).Where(p => !Path.GetFileName(p).StartsWith('.') && !p.EndsWith(".part", StringComparison.Ordinal)))
            {
                var full = Path.GetFullPath(path);
                known.TryAdd(await HashAsync(full, ct), full);
            }
        }

        scanned = true;
        logger.LogInformation("{Count} model file(s) found in {Models} and {Cache}", known.Count, options.ModelsDirectory, options.ModelCacheDirectory);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
