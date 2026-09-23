// <copyright file="LatestProgress.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>An <see cref="IProgress{T}"/> that only remembers the last value, clamped to 0..1 (read by a polling loop).</summary>
internal sealed class LatestProgress : IProgress<double>
{
    private double latest;

    public double Value => Volatile.Read(ref latest);

    public void Report(double value) => Volatile.Write(ref latest, Math.Clamp(value, 0, 1));
}
