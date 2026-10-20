// <copyright file="PanelImageLease.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>One render's use of a cached panel photo (<see cref="CachedPanelImage.TryAcquire"/>); dispose it when done.</summary>
/// <param name="image">The image, valid until the lease is disposed.</param>
/// <param name="release">Gives the use back (once).</param>
internal sealed class PanelImageLease(SKImage image, Action release) : IDisposable
{
    private Action? onRelease = release;

    /// <summary>The image, valid until the lease is disposed.</summary>
    public SKImage Image => image;

    public void Dispose() => Interlocked.Exchange(ref onRelease, null)?.Invoke();
}
