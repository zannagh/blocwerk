// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>Closes a <see cref="RecognitionWallScope"/>: the wall of the enclosing scope applies again.</summary>
internal sealed class RecognitionWallScopeHandle(Guid? previous) : IDisposable
{
    public void Dispose() => RecognitionWallScope.Ambient.Value = previous;
}
