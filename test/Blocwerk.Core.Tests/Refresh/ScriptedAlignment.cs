// <copyright file="ScriptedAlignment.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>Answers <c>AlignNormalizedAsync(photo, panel)</c> from a script keyed by the two images; null otherwise.</summary>
internal sealed class ScriptedAlignment : IImageAlignmentService
{
    private readonly List<(byte[] Photo, byte[] Panel, Homography H)> script = [];

    public ScriptedAlignment With(byte[] photo, byte[] panel, Homography h)
    {
        script.Add((photo, panel, h));
        return this;
    }

    public Task<Homography?> AlignAsync(byte[] baseImage, byte[] imageToAlign) => AlignNormalizedAsync(baseImage, imageToAlign);

    public Task<Homography?> AlignNormalizedAsync(byte[] baseImage, byte[] imageToAlign) =>
        Task.FromResult(script
            .Where(s => s.Photo.AsSpan().SequenceEqual(baseImage) && s.Panel.AsSpan().SequenceEqual(imageToAlign))
            .Select(s => (Homography?)s.H)
            .FirstOrDefault());
}
