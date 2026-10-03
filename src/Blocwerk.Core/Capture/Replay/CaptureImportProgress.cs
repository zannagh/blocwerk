// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>An open capture import as the progress API shows it.</summary>
/// <param name="ImportId">The import (= the capture's id).</param>
/// <param name="WallId">The wall it goes to.</param>
/// <param name="TotalBytes">The package's files in all.</param>
/// <param name="DoneBytes">Uploaded so far (staged, partly uploaded, or already in the store).</param>
/// <param name="StartedAt">When the import was begun.</param>
/// <param name="UpdatedAt">When a file of it was last written.</param>
public sealed record CaptureImportProgressInfo(
    Guid ImportId, Guid WallId, long TotalBytes, long DoneBytes, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Reads the open imports from their staging folders (<see cref="CapturePackageStaging"/>): the files on disk are the
/// truth of how far an upload got. A manifest is parsed once per version (it can be large) and kept in memory.
/// </summary>
public sealed class CaptureImportProgress(ICaptureFileStore files)
{
    private readonly CapturePackageStaging staging = new(files);
    private readonly ConcurrentDictionary<Guid, (DateTime Written, Guid WallId, IReadOnlyList<CapturePackageFile> Files)> manifests = new();

    /// <summary>Every open import with its upload progress.</summary>
    public async Task<IReadOnlyList<CaptureImportProgressInfo>> ListAsync(CancellationToken ct)
    {
        var ids = staging.OpenImportIds();
        foreach (var gone in manifests.Keys.Except(ids))
        {
            manifests.TryRemove(gone, out _);
        }

        var result = new List<CaptureImportProgressInfo>();
        foreach (var id in ids)
        {
            try
            {
                if (await ManifestAsync(id, ct) is { } manifest)
                {
                    result.Add(Progress(id, manifest.WallId, manifest.Files));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Committed or aborted while being read: it is simply not open any more.
            }
        }

        return result;
    }

    private async Task<(Guid WallId, IReadOnlyList<CapturePackageFile> Files)?> ManifestAsync(Guid id, CancellationToken ct)
    {
        var written = File.GetLastWriteTimeUtc(staging.ManifestPath(id));
        if (manifests.TryGetValue(id, out var cached) && cached.Written == written)
        {
            return (cached.WallId, cached.Files);
        }

        var manifest = await staging.LoadManifestAsync(id, ct);
        if (manifest is null)
        {
            return null;
        }

        var files = manifest.Files ?? [];
        manifests[id] = (written, manifest.WallId, files);
        return (manifest.WallId, files);
    }

    private CaptureImportProgressInfo Progress(Guid id, Guid wallId, IReadOnlyList<CapturePackageFile> packageFiles)
    {
        var folder = new DirectoryInfo(staging.Folder(id));
        var done = packageFiles.Sum(f => Math.Min(f.Bytes, DoneBytes(id, f)));
        var updated = folder.EnumerateFiles().Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(folder.LastWriteTimeUtc).Max();
        return new CaptureImportProgressInfo(
            id, wallId, packageFiles.Sum(f => f.Bytes), done,
            new DateTimeOffset(folder.CreationTimeUtc, TimeSpan.Zero), new DateTimeOffset(updated, TimeSpan.Zero));
    }

    /// <summary>Bytes of one file that are there: staged, partly uploaded, or in the store at the listed size.</summary>
    private long DoneBytes(Guid id, CapturePackageFile file)
    {
        var staged = new FileInfo(staging.StagedPath(id, file.Name));
        if (staged.Exists)
        {
            return staged.Length;
        }

        var stored = files.ResolvePhysicalPath(file.Name) is { } path ? new FileInfo(path) : null;
        if (stored is { Exists: true } && stored.Length == file.Bytes)
        {
            return file.Bytes;
        }

        var part = new FileInfo(staging.PartPath(id, file.Name));
        return part.Exists ? part.Length : 0;
    }
}
