// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// Hold outlines as one <c>outline-holds</c> job per call: geometry only, the caller keeps planning and writes. The job is
/// awaited without a gate slot; only the in-app run <see cref="RecognitionCaller"/> falls back to (<paramref name="inProcess"/>,
/// a <c>SessionHoldOutlineBatch</c>) takes one. The photo's size comes from its header: the outliner, like the detector,
/// ignores the EXIF orientation.
/// </summary>
public sealed class QueuedHoldOutlineBatch(IHoldOutlineBatch inProcess, RecognitionCaller caller, ILogger<QueuedHoldOutlineBatch>? logger = null)
    : IHoldOutlineBatch
{
    /// <summary>The most seeds one job carries (contracts <c>outline-request</c>); a larger call runs in the app.</summary>
    public const int MaxSeeds = 2000;

    private readonly ConcurrentDictionary<string, bool> loggedInApp = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public async Task<HoldOutlineBatchResult> OutlineAsync(
        byte[] photo, IReadOnlyList<HoldSeed> seeds, HoldOutlineBatchOptions options, CancellationToken ct = default) =>
        await RunAsync(RecognitionLane.Interactive, null, photo, seeds, options, ct)
        ?? await inProcess.OutlineAsync(photo, seeds, options, ct);

    /// <summary>
    /// Like <see cref="OutlineAsync"/> as a background job, found again after a restart by <paramref name="callerKey"/> plus a
    /// hash of the photo and the seeds (so a changed photo or target list never takes up an old job).
    /// </summary>
    public async Task<HoldOutlineBatchResult> OutlineInBackgroundAsync(
        byte[] photo, IReadOnlyList<HoldSeed> seeds, HoldOutlineBatchOptions options, string callerKey, CancellationToken ct) =>
        await RunAsync(RecognitionLane.Background, callerKey, photo, seeds, options, ct)
        ?? await inProcess.OutlineAsync(photo, seeds, options, ct);

    private async Task<HoldOutlineBatchResult?> RunAsync(
        RecognitionLane lane, string? callerKey, byte[] photo, IReadOnlyList<HoldSeed> seeds, HoldOutlineBatchOptions options, CancellationToken ct)
    {
        if (seeds.Count == 0)
        {
            return null;
        }

        var size = ImagePixelLimit.ReadSize(photo);
        var inApp = size is null ? "its photo header cannot be read" : seeds.Count > MaxSeeds ? $"it has more than {MaxSeeds} seeds" : null;
        if (inApp is not null)
        {
            if (loggedInApp.TryAdd(inApp, true))
            {
                logger?.LogWarning("An outline batch runs in the app, not on a recognition runner: {Reason} (logged once per reason)", inApp);
            }

            return null;
        }

        var request = new
        {
            version = 1,
            seeds = seeds.Select((s, i) => new { id = i, x = s.X, y = s.Y, radius = s.Radius, boxWidth = s.BoxWidth, boxHeight = s.BoxHeight }),
            options = new { joint = options.Joint },
        };
        var parts = new[] { RecognitionParts.Request(request), RecognitionParts.Image("photo", "photo.jpg", photo) };
        var key = callerKey is null ? null : $"{callerKey}:{Hash(photo, request)}";
        return await caller.RunAsync(
            RecognitionKinds.OutlineHolds,
            lane,
            key,
            parts,
            RecognitionParts.MemoryMb([photo], together: false),
            r => RecognitionResults.Outlines(r, size!.Value.Width, size.Value.Height, seeds),
            ct,
            seeds.Count);
    }

    private static string Hash(byte[] photo, object request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(photo);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(request, RecognitionParts.Json));
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }
}
