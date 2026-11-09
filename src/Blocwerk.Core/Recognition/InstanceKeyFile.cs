// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// The zero-config instance key (compose): without <c>RECOGNITION__INSTANCEKEY</c> the app generates a random key at its
/// first start into <c>RECOGNITION__INSTANCEKEYFILE</c> (mode 0600, handed to <c>RECOGNITION__INSTANCEKEYFILEOWNER</c> when
/// the app runs as root), and the recognition worker reads the same file from a read-only mount. Later starts read it back.
/// </summary>
public static class InstanceKeyFile
{
    /// <summary>The key in <paramref name="path"/>, generated first when the file does not exist.</summary>
    public static string ReadOrCreate(string path, int? owner)
    {
        if (!File.Exists(path))
        {
            Create(path, owner);
        }

        return File.ReadAllText(path).Trim();
    }

    private static void Create(string path, int? owner)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = $"{path}.{Guid.NewGuid():N}.tmp";
        var file = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            file.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(partial, file))
        {
            writer.Write(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        }

        if (owner is { } uid && !OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess && Chown(partial, uid, -1) != 0)
        {
            File.Delete(partial);
            throw new IOException($"Cannot hand {partial} to uid {uid} (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            File.Move(partial, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another replica wrote it first; its key wins.
            File.Delete(partial);
        }
    }

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int owner, int group);
}
