// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Replay;

/// <summary>
/// Replaying a finished capture on another instance without training it again (experimental, a prod copy, to prod).
/// Export: the package manifest and its files. Import: begin (dry run), upload the files, commit (rows in one
/// transaction, the model activated, the trained view as a delivered GPU job, the capture queued: the target's splat
/// worker finishes the view and the follow-up chain runs on the target's own holds). App administrators only.
/// </summary>
public interface ICapturePackageService
{
    /// <summary>The package of a finished capture. Refused (<see cref="Services.UserFacingException"/>) when it cannot be replayed.</summary>
    Task<CapturePackageManifest> ExportAsync(Guid captureId, CancellationToken ct);

    /// <summary>The physical path of one of the package's files, or null when it is not one of them or is gone.</summary>
    Task<string?> ExportFilePathAsync(Guid captureId, string name, CancellationToken ct);

    /// <summary>Checks the package against this instance (the dry run) and opens the import (its id is the capture id).</summary>
    Task<CaptureImportReport> BeginImportAsync(CapturePackageManifest manifest, CancellationToken ct);

    /// <summary>Streams one file into the import, verified against the manifest's size and SHA-256.</summary>
    Task<CaptureImportFile> PutFileAsync(Guid importId, string name, Stream body, CancellationToken ct);

    /// <summary>Checks again and, when nothing blocks and every file is there, inserts the rows and queues the capture.</summary>
    Task<CaptureImportReport> CommitImportAsync(Guid importId, CancellationToken ct);

    /// <summary>Drops an open import and its uploaded files (nothing in the database is touched).</summary>
    Task AbortImportAsync(Guid importId, CancellationToken ct);
}
