// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// A finished capture as a "capture package": its rows (with the source's ids and stored file names, so every cross
/// reference inside the JSON stays valid) and the list of its files. Another instance replays it without training
/// (<see cref="ICapturePackageService"/>): the trained view is finished on its splat worker and the follow-up chain
/// runs on its own holds.
/// </summary>
public sealed record CapturePackageManifest
{
    /// <summary>The format this build writes and reads.</summary>
    public const int CurrentFormat = 1;

    public int FormatVersion { get; init; } = CurrentFormat;

    /// <summary>The source app's informational version (with the commit when the build stamps it).</summary>
    public string? SourceAppVersion { get; init; }

    /// <summary>The newest migration the source build knows: the rows' schema.</summary>
    public string? SourceMigration { get; init; }

    public DateTimeOffset ExportedAt { get; init; }

    public Guid CaptureId { get; init; }

    public Guid WallId { get; init; }

    /// <summary>The admin who started the capture; the target's pipeline runs as this user.</summary>
    public Guid OwnerUserId { get; init; }

    /// <summary>The marker plan revision the capture was solved with (null: none).</summary>
    public int? PlanRevision { get; init; }

    /// <summary>That plan revision's JSON on the source, compared with the target's.</summary>
    public string? PlanJson { get; init; }

    /// <summary>The model the capture's model was registered to (same frame), if any.</summary>
    public Guid? ReferenceModelId { get; init; }

    public required CapturePackageRows Rows { get; init; }

    public required IReadOnlyList<CapturePackageFile> Files { get; init; }

    /// <summary>What the export noticed (files left out, photo retention).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>The rows a replay inserts. The trained view's GPU job carries the prepared state and the runner's result.</summary>
public sealed record CapturePackageRows(
    WallCapture Capture,
    IReadOnlyList<WallCapturePhoto> Photos,
    WallGeometryModel Model,
    IReadOnlyList<WallGeometryTexture> Textures,
    GpuJob GpuJob);

/// <summary>One stored file of the package: its bare name in the capture store, size, SHA-256 (lowercase hex) and what it is.</summary>
public sealed record CapturePackageFile(string Name, long Bytes, string Sha256, string Role);

/// <summary>Where a package file stands on the target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaptureImportFileState>))]
public enum CaptureImportFileState
{
    /// <summary>Not uploaded yet.</summary>
    Missing,

    /// <summary>Uploaded and hash-verified, waiting in the import's staging folder for the commit.</summary>
    Staged,

    /// <summary>Already in the capture store with the same hash (an earlier import): not uploaded again.</summary>
    Present,

    /// <summary>A different file with the same name is in the capture store: the import cannot go ahead.</summary>
    Conflict,
}

/// <summary>A package file's state on the target.</summary>
public sealed record CaptureImportFile(string Name, long Bytes, CaptureImportFileState State);

/// <summary>
/// The dry-run report of an import (and the commit's answer). <see cref="Blockers"/> stop the commit;
/// <see cref="Warnings"/> do not.
/// </summary>
public sealed record CaptureImportReport(
    Guid ImportId,
    Guid CaptureId,
    Guid WallId,
    bool AlreadyImported,
    bool Committed,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CaptureImportFile> Files,
    long MissingBytes,
    long? FreeBytes)
{
    /// <summary>No blockers: the files can be uploaded and the import committed.</summary>
    public bool CanCommit => Blockers.Count == 0;

    /// <summary>No blockers and every file is uploaded (or already there).</summary>
    public bool ReadyToCommit => CanCommit && Files.All(f => f.State is CaptureImportFileState.Staged or CaptureImportFileState.Present);
}
