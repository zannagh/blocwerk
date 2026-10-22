// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// A file version as the file system reports it: path, length and last write time. The key of
/// <see cref="FileHashMemo"/>; a rewrite in place moves the write time, a replace by rename moves it too.
/// </summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="WriteTicks">Its last write time (UTC ticks).</param>
public readonly record struct FileStamp(string Path, long Length, long WriteTicks)
{
    /// <summary>The current stamp of <paramref name="path"/>, or null when there is no such file.</summary>
    public static FileStamp? Of(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(path, info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }
}
