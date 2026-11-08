// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The wall whose photos the recognition calls of the current flow look at. The recognition interfaces take only images,
/// but a queued job needs its wall: only runners that may serve that wall see its photos. A call outside any scope runs in
/// the app, as before the queue.
/// </summary>
public static class RecognitionWallScope
{
    internal static readonly AsyncLocal<Guid?> Ambient = new();

    /// <summary>Gets the wall of the innermost open scope, or null.</summary>
    public static Guid? Current => Ambient.Value;

    /// <summary>Opens a scope for <paramref name="wallId"/>; dispose it when the flow is done with that wall.</summary>
    public static IDisposable Begin(Guid wallId)
    {
        var previous = Ambient.Value;
        Ambient.Value = wallId;
        return new RecognitionWallScopeHandle(previous);
    }
}
