// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>
/// Pacing of <see cref="ContentHashBackfill"/>, bound from the <c>ContentHashBackfill</c> configuration section (env
/// <c>ContentHashBackfill__Enabled=false</c> switches it off without a deploy, at the next restart). The defaults are the
/// production ones; tests shorten the waits.
/// </summary>
public sealed record ContentHashBackfillOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ContentHashBackfill";

    /// <summary>Gets a value indicating whether the hosted service runs at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the wait after startup before the first pass, so the backfill does not compete with the warm-up.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the wait before another pass, when the last one left rows that were locked or changed meanwhile.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how many passes one start runs at most; the next start picks up whatever is still left.</summary>
    public int MaxPasses { get; init; } = 4;

    /// <summary>Gets the rows of the first database batch; the pacer moves it between 1 and <see cref="MaxBlobBatch"/>.</summary>
    public int InitialBlobBatch { get; init; } = 4;

    /// <summary>Gets the most rows one database batch hashes (small blobs such as avatars and thumbnails reach it).</summary>
    public int MaxBlobBatch { get; init; } = 64;

    /// <summary>
    /// Gets the most blob bytes one database batch hashes (the first row always goes, however large): about nine 7 MB
    /// photos, so a cold batch keeps its rows locked well under a second. On Postgres the bytes never leave the database;
    /// this bounds the TOAST reads and the hashing inside the one statement.
    /// </summary>
    public long MaxBlobBatchBytes { get; init; } = 64L * 1024 * 1024;

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
