// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The JS calls behind <see cref="OfflineStorageCard"/>. Every one may fail without failing the circuit: the JS can
/// throw, the circuit can go away, and the interop can time out. report() waits for the cache to start, and a clear
/// waits for the running fill, so a large splat download can outlast the interop timeout.
/// </summary>
internal sealed class OfflineStorageInterop(IJSRuntime js)
{
    /// <summary>What the card shows when the browser could not answer: no caching, 3D at its default.</summary>
    public static OfflineStorageFacts Unavailable { get; } =
        new(false, false, false, true, false, false, false, null, false, 0, []);

    /// <summary>Whether <paramref name="ex"/> is an interop failure the card absorbs (the repo's pattern).</summary>
    public static bool IsInteropFailure(Exception ex) =>
        ex is JSException or JSDisconnectedException or TaskCanceledException;

    /// <summary><c>bwAssetReport.report()</c>, or null when the browser could not answer.</summary>
    public async Task<OfflineStorageFacts?> ReportAsync()
    {
        try
        {
            return await js.InvokeAsync<OfflineStorageFacts?>("bwAssetReport.report");
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Runs a void JS call; false when it failed.</summary>
    public async Task<bool> RunAsync(string identifier, params object?[] args)
    {
        try
        {
            await js.InvokeVoidAsync(identifier, args);
            return true;
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return false;
        }
    }
}
