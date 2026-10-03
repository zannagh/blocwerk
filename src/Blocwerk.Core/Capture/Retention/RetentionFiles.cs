// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Retention;

/// <summary>Measuring and deleting capture store files for the retention rules: missing files count 0, failures are logged.</summary>
public static class RetentionFiles
{
    /// <summary>The bytes the stored files take now (missing ones count 0; each name once).</summary>
    public static long SizeOf(ICaptureFileStore files, IEnumerable<string> names) =>
        names.Distinct(StringComparer.Ordinal).Sum(name => files.ResolvePhysicalPath(name) is { } path && File.Exists(path) ? new FileInfo(path).Length : 0);

    /// <summary>
    /// Deletes the stored files (each name once) and returns the bytes freed. A failed delete is logged and left to the
    /// orphan sweep; a missing file is fine (the delete already happened before a restart).
    /// </summary>
    public static long Delete(ICaptureFileStore files, IEnumerable<string> names, ILogger logger)
    {
        long freed = 0;
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var size = SizeOf(files, [name]);
            try
            {
                files.Delete(name);
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete capture file {File}; the next sweep retries", name);
            }
        }

        return freed;
    }

    /// <summary>The size in bytes of every file under <paramref name="folder"/> (0 when it is gone).</summary>
    public static long FolderSize(string folder) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    /// <summary>Human-readable size for the log.</summary>
    public static string Format(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.##} GB"
        : $"{bytes / (double)(1L << 20):0.#} MB";
}
