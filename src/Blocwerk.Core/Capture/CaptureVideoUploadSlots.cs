// <copyright file="CaptureVideoUploadSlots.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>
/// Bounds the walk-along video uploads streaming in at once: one per user and a few server-wide. Each
/// upload may write up to <see cref="WallCapturePipelineOptions.MaxVideoBytes"/> to disk and holds the
/// deploy gate while it streams, so without a bound any wall admin (and anyone may create a wall) could
/// open dozens of parallel 2 GB uploads to fill the disk or keep the app "busy". In-process, like the
/// rest of the single-instance app: registered as a singleton.
/// </summary>
public sealed class CaptureVideoUploadSlots
{
    /// <summary>Uploads one user may have streaming at once.</summary>
    public const int PerUser = 1;

    /// <summary>Uploads the whole server accepts at once.</summary>
    public const int Global = 4;

    private readonly object gate = new();
    private readonly Dictionary<Guid, int> perUser = [];
    private readonly int global;
    private int active;

    public CaptureVideoUploadSlots(int global = Global)
    {
        this.global = global;
    }

    /// <summary>Takes a slot for <paramref name="userId"/>; null when the user or the server is at its limit.</summary>
    public IDisposable? TryAcquire(Guid userId)
    {
        lock (gate)
        {
            var mine = perUser.GetValueOrDefault(userId);
            if (mine >= PerUser || active >= global)
            {
                return null;
            }

            perUser[userId] = mine + 1;
            active++;
            return new Slot(this, userId);
        }
    }

    private void Release(Guid userId)
    {
        lock (gate)
        {
            active--;
            if (perUser.TryGetValue(userId, out var mine) && mine > 1)
            {
                perUser[userId] = mine - 1;
            }
            else
            {
                perUser.Remove(userId);
            }
        }
    }

    private sealed class Slot(CaptureVideoUploadSlots owner, Guid userId) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Release(userId);
            }
        }
    }
}
