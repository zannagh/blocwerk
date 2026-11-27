// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The answer to one file's PUT: that file is in place and verified. The whole scan's state is a GET away.</summary>
/// <param name="ScanId">The scan.</param>
/// <param name="Name">The package file name.</param>
/// <param name="Bytes">Its size, as the manifest lists it.</param>
/// <param name="Status">Always <see cref="Received"/>; a refusal is an error instead.</param>
public sealed record ScanFileState(Guid ScanId, string Name, long Bytes, string Status = ScanFileState.Received)
{
    public const string Received = "received";
}
