// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Assets.Partition;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The partition key (<see cref="ICachePartitionKey"/>): 32 random bytes, kept in <paramref name="keyFile"/> protected with
/// the app's DataProtection key ring (purpose <c>blocwerk.cache-partition</c>), so the file alone is useless. Created on
/// first use. In production the file sits beside the key ring on the persisted <c>/app/keys</c> volume (the ring reads
/// only its <c>*.xml</c> files there).
/// </summary>
/// <remarks>
/// Never fatal: a file that cannot be read, unprotected or written is replaced by a fresh key (logged). A new key gives
/// every device a new label, which only makes each of them wipe and refill its asset cache once. With no
/// <paramref name="keyFile"/> (Development) the key lives in memory, so labels change with each restart.
/// </remarks>
public sealed class DataProtectionCachePartitionKey(
    IDataProtectionProvider dataProtectionProvider,
    string? keyFile,
    ILogger<DataProtectionCachePartitionKey> logger) : ICachePartitionKey
{
    private const string Purpose = "blocwerk.cache-partition";
    private const int KeyBytes = 32;

    private readonly Lazy<byte[]> key = new(() => Load(dataProtectionProvider.CreateProtector(Purpose), keyFile, logger));

    /// <inheritdoc/>
    public byte[] Key => key.Value;

    private static byte[] Load(IDataProtector protector, string? path, ILogger logger)
    {
        if (path is null)
        {
            logger.LogInformation("No cache-partition key file is configured; partition labels last until the next restart");
            return RandomNumberGenerator.GetBytes(KeyBytes);
        }

        try
        {
            if (File.Exists(path))
            {
                if (Read(protector, path) is { } stored)
                {
                    return stored;
                }

                logger.LogWarning("The cache-partition key at {Path} cannot be unprotected; replacing it", path);
            }

            var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
            Write(protector, path, fresh);
            logger.LogInformation("Created the cache-partition key at {Path}", path);
            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read or write the cache-partition key at {Path}; using a key for this run only", path);
            return RandomNumberGenerator.GetBytes(KeyBytes);
        }
    }

    private static byte[]? Read(IDataProtector protector, string path)
    {
        try
        {
            var bytes = Convert.FromBase64String(protector.Unprotect(File.ReadAllText(path).Trim()));
            return bytes.Length == KeyBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static void Write(IDataProtector protector, string path, byte[] secret)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, protector.Protect(Convert.ToBase64String(secret)));
        File.Move(temp, path, overwrite: true);
    }
}
