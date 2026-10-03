// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// An open import's folder: <c>captures/imports/{importId}/</c> with the manifest and the uploaded, hash-verified files.
/// A sub-folder on purpose: the capture sweep only looks at the store's top level, so nothing staged here is taken for an
/// orphan however long the upload takes. The commit moves the files up next to the rows that reference them; an import
/// left without activity is removed by <see cref="Retention.ImportStagingRetention"/>.
/// </summary>
internal sealed class CapturePackageStaging(ICaptureFileStore files)
{
    private const string ManifestFile = "manifest.json";
    private const string PartSuffix = ".part";

    public string Folder(Guid importId) => Path.Combine(Root(), importId.ToString("N"));

    public string StagedPath(Guid importId, string name) => Path.Combine(Folder(importId), name);

    public string PartPath(Guid importId, string name) => StagedPath(importId, name) + PartSuffix;

    /// <summary>Stores the manifest; a staged file whose hash changed against the previous manifest is dropped.</summary>
    public async Task SaveManifestAsync(CapturePackageManifest manifest, CancellationToken ct)
    {
        var importId = manifest.CaptureId;
        Directory.CreateDirectory(Folder(importId));
        if (await LoadManifestAsync(importId, ct) is { } previous)
        {
            var now = manifest.Files.ToDictionary(f => f.Name, f => f.Sha256, StringComparer.Ordinal);
            foreach (var old in previous.Files.Where(f => !now.TryGetValue(f.Name, out var sha) || sha != f.Sha256))
            {
                File.Delete(StagedPath(importId, old.Name));
            }
        }

        await File.WriteAllTextAsync(Path.Combine(Folder(importId), ManifestFile), JsonSerializer.Serialize(manifest, CapturePackageFiles.Json), ct);
    }

    /// <summary>The open import's manifest, or null when there is none.</summary>
    public async Task<CapturePackageManifest?> LoadManifestAsync(Guid importId, CancellationToken ct)
    {
        var path = ManifestPath(importId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CapturePackageManifest>(stream, CapturePackageFiles.Json, ct);
    }

    /// <summary>Removes the import's folder (after the commit, or to abandon it).</summary>
    public void Delete(Guid importId)
    {
        var folder = Folder(importId);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The ids of every open import (a folder with a manifest), for the progress API.</summary>
    public IReadOnlyList<Guid> OpenImportIds()
    {
        var root = Root();
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Select(d => Guid.TryParseExact(Path.GetFileName(d), "N", out var id) && File.Exists(ManifestPath(id)) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
    }

    /// <summary>The path of an import's manifest.</summary>
    public string ManifestPath(Guid importId) => Path.Combine(Folder(importId), ManifestFile);

    /// <summary>Every import folder there is (open, finished with or abandoned), by import id.</summary>
    public IReadOnlyList<Guid> List()
    {
        var root = Root();
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Select(d => Guid.TryParseExact(Path.GetFileName(d), "N", out var id) ? id : (Guid?)null)
                .OfType<Guid>()
                .ToList()
            : [];
    }

    /// <summary>When anything in the import's folder (the folder itself, the manifest, a file, a partial upload) last changed.</summary>
    public DateTimeOffset? LastActivity(Guid importId)
    {
        var folder = new DirectoryInfo(Folder(importId));
        if (!folder.Exists)
        {
            return null;
        }

        var last = folder.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Select(f => f.LastWriteTimeUtc)
            .Append(folder.LastWriteTimeUtc)
            .Max();
        return new DateTimeOffset(last, TimeSpan.Zero);
    }

    /// <summary>The capture store's physical path of a stored name.</summary>
    public string StorePath(string name) =>
        files.ResolvePhysicalPath(name) ?? throw new InvalidOperationException($"{name} is not a capture store name.");

    /// <summary>The capture store's folder (for the free-space check).</summary>
    public string StoreFolder() => Path.GetDirectoryName(StorePath("probe.bin"))!;

    private string Root() => Path.Combine(StoreFolder(), "imports");
}
