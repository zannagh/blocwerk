// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall-image store that runs <see cref="OnResolve"/> (once) when a stored name is resolved: the backfill resolves a
/// file after reading its row and before hashing it, so this is where a concurrent rewrite lands in that window.
/// </summary>
internal sealed class HookedWallImageStorage(IWallImageStorage inner) : IWallImageStorage
{
    public Action<string>? OnResolve { get; set; }

    public string CreateTempPath(string extension) => inner.CreateTempPath(extension);

    public string Commit(string tempPath, string extension) => inner.Commit(tempPath, extension);

    public string? ResolvePhysicalPath(string storedName)
    {
        var hook = OnResolve;
        OnResolve = null;
        hook?.Invoke(storedName);
        return inner.ResolvePhysicalPath(storedName);
    }

    public void Delete(string? storedName) => inner.Delete(storedName);
}
