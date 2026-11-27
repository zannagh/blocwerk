// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Entities;

/// <summary>How a <see cref="Wall"/> gets updated. Stored as its integer value; never renumber.</summary>
public enum WallUpdateMode
{
    /// <summary>Panel photos, the wall update as it always was (the default).</summary>
    Panels = 0,

    /// <summary>iPhone scans from the companion app may be uploaded for this wall ("iPhone scan").</summary>
    Scan = 1,
}
