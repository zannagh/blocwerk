// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A feed that, once armed, runs a write right after the next mark it hands out: the write lands between a document's
/// cursor read and its build, the race the build cursor exists for.
/// </summary>
internal sealed class WriteAfterMarkFeed(IWallFeed inner) : IWallFeed
{
    private Func<Task>? armed;
    private int marks;

    /// <summary>Gets how many marks were handed out: one per document build.</summary>
    public int Marks => Volatile.Read(ref marks);

    /// <summary>Runs <paramref name="write"/> once, after the next <see cref="CurrentAsync"/>.</summary>
    public void Arm(Func<Task> write) => armed = write;

    public Task<Guid> InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);

    public async Task<FeedCursor> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var mark = await inner.CurrentAsync(cancellationToken);
        Interlocked.Increment(ref marks);
        if (Interlocked.Exchange(ref armed, null) is { } write)
        {
            await write();
        }

        return mark;
    }

    public Task<FeedPage> ReadAsync(FeedFilter filter, FeedCursor after, int limit, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(filter, after, limit, cancellationToken);

    public Task<long?> AppendAsync(BlocwerkDbContext db, WallChange row, CancellationToken cancellationToken = default) =>
        inner.AppendAsync(db, row, cancellationToken);
}
