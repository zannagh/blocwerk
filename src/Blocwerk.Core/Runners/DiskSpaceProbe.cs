// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>Free space of the drive a directory lives on (overridable for tests).</summary>
public class DiskSpaceProbe
{
    /// <summary>Free bytes available to this process, or null when the drive cannot be read (then nothing is refused).</summary>
    public virtual long? FreeBytes(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
