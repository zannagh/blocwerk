// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// The model files <c>GET /api/runners/models/{sha256}</c> may serve (runner protocol 1.2): the files directly in
/// <see cref="RecognitionQueueOptions.ModelsDirectory"/>, by SHA-256, hashed once at start. A link pointing out of the
/// directory is skipped, and a file changed since it was hashed is not served (its hash no longer holds).
/// </summary>
public sealed class RecognitionModelCatalog(RecognitionQueueOptions options, ILogger<RecognitionModelCatalog> logger)
{
    private readonly Lazy<Task<IReadOnlyDictionary<string, (string Path, long Length, DateTime WrittenAt)>>> models =
        new(() => Task.Run(() => Scan(options.ModelsDirectory, logger)));

    /// <summary>Starts hashing in the background.</summary>
    public void Warm() => _ = models.Value;

    /// <summary>The path of the model with this lower-case hex SHA-256, or null.</summary>
    public async Task<string?> FindAsync(string sha256)
    {
        var known = await models.Value;
        if (!known.TryGetValue(sha256, out var model))
        {
            return null;
        }

        var info = new FileInfo(model.Path);
        return info.Exists && info.Length == model.Length && info.LastWriteTimeUtc == model.WrittenAt ? model.Path : null;
    }

    private static IReadOnlyDictionary<string, (string Path, long Length, DateTime WrittenAt)> Scan(string directory, ILogger logger)
    {
        var found = new Dictionary<string, (string Path, long Length, DateTime WrittenAt)>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            logger.LogInformation("No models directory at {Directory}: runners can download no models", directory);
            return found;
        }

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            var target = info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (info.Name.StartsWith('.') || target is null || !target.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                found[hash] = (path, info.Length, info.LastWriteTimeUtc);
                logger.LogInformation("Model {Name} is served to runners as {Sha256}", info.Name, hash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Model {Name} cannot be read; runners cannot download it", info.Name);
            }
        }

        return found;
    }
}
