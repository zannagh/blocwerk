// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// Receiving an iPhone scan package (companion app): begin with the manifest, put each missing file, commit. Re-running
/// any call is safe; a re-run begin answers with only the files still missing. Only the wall's owner, admins and
/// moderators, and administrators of the installation, may upload; never from a kiosk. Throws
/// <see cref="ScanUploadException"/> for a refusal and <see cref="UnauthorizedAccessException"/> for a caller who may not.
/// </summary>
public interface IWallScanService
{
    Task<ScanUploadState> BeginAsync(Guid wallId, ScanManifest manifest, CancellationToken ct);

    /// <summary>
    /// The checks of a begin that need no manifest: the caller may upload to the wall, and the wall is set to "iPhone
    /// scan". The route runs them before it reads the manifest's body.
    /// </summary>
    Task EnsureMayBeginAsync(Guid wallId, CancellationToken ct);

    /// <summary>
    /// Takes one file as a stream, verified against the manifest's size and SHA-256 (and <paramref name="sha256"/>, when
    /// the caller sends it). A file already received is not read again. Answers with that file only; the files of one
    /// scan may be sent in parallel. Only the user who began the scan, or an administrator of the installation, may send.
    /// </summary>
    Task<ScanFileState> PutFileAsync(Guid wallId, Guid scanId, string name, string? sha256, long? length, Stream body, CancellationToken ct);

    /// <summary>Marks the scan received once every file is there; 409 with the missing files otherwise.</summary>
    Task<ScanUploadState> CommitAsync(Guid wallId, Guid scanId, CancellationToken ct);

    Task<ScanUploadState> GetAsync(Guid wallId, Guid scanId, CancellationToken ct);

    /// <summary>The wall's ten most recent scans, newest first.</summary>
    Task<IReadOnlyList<WallScanSummary>> ListAsync(Guid wallId, CancellationToken ct);
}
