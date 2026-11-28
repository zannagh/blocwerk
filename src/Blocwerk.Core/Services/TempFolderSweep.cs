// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services;

/// <summary>Deletes the stale files of an upload temp folder (top level only; a file in use is skipped).</summary>
public static class TempFolderSweep
{
    /// <summary>Deletes the files in <paramref name="folder"/> last written before <paramref name="olderThan"/>; returns how many.</summary>
    public static int Sweep(string folder, DateTimeOffset olderThan)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < olderThan.UtcDateTime)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // Still open by a running upload, or gone already: the next sweep looks again.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours to delete.
            }
        }

        return deleted;
    }
}
