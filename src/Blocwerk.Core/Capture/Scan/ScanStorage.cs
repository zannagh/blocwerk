// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// A scan's folder in the capture store: <c>captures/scans/{scanId}/</c>, the package files under their package names
/// (<c>video/seg0-000.mov</c>, …) and, once processing exists, its derived files. A sub-folder on purpose, like the
/// capture imports: the capture sweep only looks at the store's top level, so nothing here is taken for an orphan.
/// <see cref="Retention.ScanUploadRetention"/> removes the folders of abandoned uploads.
/// </summary>
public sealed class ScanStorage(ICaptureFileStore files)
{
    /// <summary>
    /// The reserved place of the scan's cleaned point cloud (<see cref="Entities.WallScan.PointCloudPath"/>), written by
    /// processing. Not a package name, so no upload can write it.
    /// </summary>
    public const string PointCloudFile = "derived/point-cloud.ply";

    private const string PartSuffix = ".part";

    /// <summary>False for a store without a folder (test stores): then there is nothing to keep or sweep.</summary>
    public bool HasFolder => files.ResolvePhysicalPath("probe.bin") is not null;

    public string Folder(Guid scanId) => Path.Combine(Root(), scanId.ToString("N"));

    /// <summary>The path of a package file. The name must already be a valid package name (<see cref="ScanFileNames"/>).</summary>
    public string FilePath(Guid scanId, string name)
    {
        if (ScanFileNames.KindOf(name) == ScanFileKind.Invalid && name != PointCloudFile)
        {
            throw new ArgumentException($"{name} is not a scan package name.", nameof(name));
        }

        return Path.Combine(Folder(scanId), name.Replace('/', Path.DirectorySeparatorChar));
    }

    public string PartPath(Guid scanId, string name) => FilePath(scanId, name) + PartSuffix;

    /// <summary>True when the file is in the folder with the expected size (its hash was checked when it arrived).</summary>
    public bool Has(Guid scanId, string name, long bytes)
    {
        var info = new FileInfo(FilePath(scanId, name));
        return info.Exists && info.Length == bytes;
    }

    public void Delete(Guid scanId)
    {
        var folder = Folder(scanId);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Every scan folder there is, by scan id.</summary>
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

    /// <summary>When anything in the scan's folder last changed, or null when there is no folder.</summary>
    public DateTimeOffset? LastActivity(Guid scanId)
    {
        var folder = new DirectoryInfo(Folder(scanId));
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

    /// <summary>Free bytes on the store's volume (the scans folder is made first: a drive is looked up by an existing path).</summary>
    public long FreeBytes()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private string Root()
    {
        var probe = files.ResolvePhysicalPath("probe.bin") ?? throw new InvalidOperationException("The capture store has no folder.");
        return Path.Combine(Path.GetDirectoryName(probe)!, "scans");
    }
}
