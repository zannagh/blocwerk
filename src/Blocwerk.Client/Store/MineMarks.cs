// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Mine;

namespace Blocwerk.Client.Store;

/// <summary>
/// The signed-in viewer's own marks on one wall, as the boulder list and detail read them. A flash counts as a send
/// (the server reports <c>Sent</c> only for an entry of type send; a flash is a send on the first go).
/// </summary>
public sealed class MineMarks
{
    private readonly IReadOnlyDictionary<Guid, MineDocumentTick> ticks;
    private readonly IReadOnlySet<Guid> favourites;

    /// <summary>Initializes a new instance of the <see cref="MineMarks"/> class.</summary>
    /// <param name="cursor">The feed cursor the marks were read at.</param>
    /// <param name="ticks">The viewer's log per boulder.</param>
    /// <param name="favourites">The viewer's favourites.</param>
    public MineMarks(string? cursor, IEnumerable<MineDocumentTick> ticks, IEnumerable<Guid> favourites)
    {
        Cursor = cursor;
        this.ticks = ticks.GroupBy(t => t.BoulderId).ToDictionary(g => g.Key, g => g.Last());
        this.favourites = favourites.ToHashSet();
    }

    /// <summary>Gets no marks: a device that may hold none (anonymous, kiosk) or has none yet.</summary>
    public static MineMarks None { get; } = new(null, [], []);

    /// <summary>Gets the feed cursor the marks were read at.</summary>
    public string? Cursor { get; }

    /// <summary>Gets the number of boulders with a log entry.</summary>
    public int Count => ticks.Count;

    /// <summary>The marks of a stored mine row.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Its marks.</returns>
    public static MineMarks From(StoreMineRow row) => new(row.Cursor, row.Ticks, row.Favourites);

    /// <summary>The marks of a fetched mine document (the sync loop, PR 3.12).</summary>
    /// <param name="document">The document.</param>
    /// <returns>Its marks.</returns>
    public static MineMarks From(MineDocument document) => new(document.Cursor, document.Ticks, document.Favourites);

    /// <summary>The viewer's log on a boulder, or null.</summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>The tick, or null.</returns>
    public MineDocumentTick? TickFor(Guid boulderId) => ticks.GetValueOrDefault(boulderId);

    /// <summary>True when the viewer sent the boulder, a flash included.</summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>Whether it counts as sent.</returns>
    public bool IsSent(Guid boulderId) => TickFor(boulderId) is { } tick && (tick.Sent || tick.Flashed);

    /// <summary>True when the viewer flashed the boulder.</summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>Whether it was flashed.</returns>
    public bool IsFlashed(Guid boulderId) => TickFor(boulderId)?.Flashed == true;

    /// <summary>True when the boulder is one of the viewer's favourites.</summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>Whether it is a favourite.</returns>
    public bool IsFavourite(Guid boulderId) => favourites.Contains(boulderId);
}
