// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>What a package file is, by its folder.</summary>
public enum ScanFileKind
{
    /// <summary>Not a name the package may use.</summary>
    Invalid = 0,

    Video = 1,

    Depth = 2,

    Confidence = 3,

    Mesh = 4,
}
