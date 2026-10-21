// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>Pacing of <see cref="ContentHashBackfill"/>. The defaults are the production ones; tests shorten the waits.</summary>
public sealed record ContentHashBackfillOptions
{
    /// <summary>Gets the wait after startup before the first pass, so the backfill does not compete with the warm-up.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the wait before another pass, when the last one left rows that were locked or changed meanwhile.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how many passes one start runs at most; the next start picks up whatever is still left.</summary>
    public int MaxPasses { get; init; } = 4;

    /// <summary>Gets the rows of the first database batch; the pacer moves it between 1 and <see cref="MaxBlobBatch"/>.</summary>
    public int InitialBlobBatch { get; init; } = 4;

    /// <summary>Gets the most rows one database batch hashes (a batch of 7 MB photos is about 450 MB at this size).</summary>
    public int MaxBlobBatch { get; init; } = 64;

    /// <summary>Gets how many file rows one read fetches; the files are then hashed one at a time.</summary>
    public int FileBatch { get; init; } = 32;

    /// <summary>
    /// Gets the time one database batch should take: a slower one halves the next batch, a much faster one doubles it.
    /// The rows of a batch stay locked this long (never the whole table), so it bounds how long a writer waits on one.
    /// </summary>
    public TimeSpan BatchTarget { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the shortest pause after a batch or a file; the pause is never shorter than the work before it.</summary>
    public TimeSpan MinPause { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gets how often a long-running column logs its progress.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(30);
}
