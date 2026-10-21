// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Backfill;

/// <summary>
/// Throttles the backfill: sizes the next database batch from how long the last one took (halved above
/// <see cref="ContentHashBackfillOptions.BatchTarget"/>, doubled below a quarter of it), and pauses after every batch or
/// file at least as long as the work took, so the backfill uses at most about half of one database or app core.
/// </summary>
internal sealed class BackfillPacer(ContentHashBackfillOptions options)
{
    /// <summary>Gets the rows the next database batch takes.</summary>
    public int BatchSize { get; private set; } = Math.Clamp(options.InitialBlobBatch, 1, options.MaxBlobBatch);

    /// <summary>Resizes the next batch from <paramref name="took"/>.</summary>
    public void Measure(TimeSpan took)
    {
        if (took > options.BatchTarget)
        {
            BatchSize = Math.Max(1, BatchSize / 2);
        }
        else if (took < options.BatchTarget / 4)
        {
            BatchSize = Math.Min(options.MaxBlobBatch, BatchSize * 2);
        }
    }

    /// <summary>The pause after work that took <paramref name="took"/>.</summary>
    public TimeSpan PauseAfter(TimeSpan took) => took > options.MinPause ? took : options.MinPause;

    /// <summary>Waits <see cref="PauseAfter"/>.</summary>
    public Task PauseAsync(TimeSpan took, CancellationToken ct)
    {
        var pause = PauseAfter(took);
        return pause > TimeSpan.Zero ? Task.Delay(pause, ct) : Task.CompletedTask;
    }
}
