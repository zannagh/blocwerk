// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>
/// Where a scan upload stands: <c>uploading</c> (begun, files outstanding or not yet committed) or <c>received</c>
/// (complete and verified, waiting for processing), with the files still to send.
/// </summary>
/// <param name="ScanId">The scan (and capture) id: the manifest's <c>scanId</c>.</param>
/// <param name="WallId">The wall.</param>
/// <param name="Status"><c>uploading</c> or <c>received</c>.</param>
/// <param name="Stage">What the wall's capture page shows.</param>
/// <param name="Missing">Package files not received yet; send exactly these.</param>
/// <param name="TotalFiles">Files in the package.</param>
/// <param name="TotalBytes">Bytes in the package.</param>
/// <param name="ReceivedBytes">Bytes received and verified so far.</param>
public sealed record ScanUploadState(
    Guid ScanId,
    Guid WallId,
    string Status,
    string? Stage,
    IReadOnlyList<string> Missing,
    int TotalFiles,
    long TotalBytes,
    long ReceivedBytes)
{
    public const string Uploading = "uploading";

    public const string Received = "received";
}
