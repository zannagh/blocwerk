// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>Only the <c>files</c> list of a stored manifest, for <see cref="ScanFileTables"/>; the rest is skipped.</summary>
/// <param name="Files">The package files, as in <see cref="ScanManifest.Files"/>.</param>
internal sealed record ScanManifestFiles(IReadOnlyList<ScanFileEntry>? Files);
