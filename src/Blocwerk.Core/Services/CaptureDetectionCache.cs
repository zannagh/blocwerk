// <copyright file="CaptureDetectionCache.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Geometry.Proposals;

namespace Blocwerk.Core.Services;

/// <summary>
/// Detections per stored capture photo and detector, kept in memory so a second hold search (after hiding a
/// volume, reviewing proposals) skips the minutes of detection. Stored photos are content-addressed and never
/// change under their name. Small: ~250 detections per photo; cleared when it grows past <see cref="MaxPhotos"/>.
/// </summary>
internal static class CaptureDetectionCache
{
    /// <summary>Most photos kept.</summary>
    public const int MaxPhotos = 1000;

    private static readonly ConcurrentDictionary<(string Path, string Detector), IReadOnlyList<CaptureDetection>> Entries = new();

    /// <summary>The cached detections, or null.</summary>
    /// <param name="path">Stored photo path.</param>
    /// <param name="detector">Detector name.</param>
    /// <returns>The detections or null.</returns>
    public static IReadOnlyList<CaptureDetection>? Get(string path, string detector) => Entries.GetValueOrDefault((path, detector));

    /// <summary>Remembers detections.</summary>
    /// <param name="path">Stored photo path.</param>
    /// <param name="detector">Detector name.</param>
    /// <param name="detections">The detections.</param>
    public static void Put(string path, string detector, IReadOnlyList<CaptureDetection> detections)
    {
        if (Entries.Count >= MaxPhotos)
        {
            Entries.Clear();
        }

        Entries[(path, detector)] = detections;
    }
}
