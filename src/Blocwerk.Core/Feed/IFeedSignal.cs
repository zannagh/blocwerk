// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.Channels;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The wake-up channel between writers and the <see cref="WallFeedHub"/> (design §5.2, §5.10). A wake carries no data
/// and never advances a cursor: the hub reads the rows below the horizon itself, so a lost wake costs latency (the
/// safety poll catches up), never a row. In-process today; a later Postgres implementation would NOTIFY on
/// <see cref="Wake"/> and feed <see cref="Wakes"/> from a LISTEN connection.
/// </summary>
public interface IFeedSignal
{
    /// <summary>Signals new committed rows. Never blocks and never throws at the writer.</summary>
    void Wake(FeedWake wake);

    /// <summary>Gets the wakes, for the hub's loop.</summary>
    ChannelReader<FeedWake> Wakes { get; }
}
