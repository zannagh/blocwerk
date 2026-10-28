// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Mine;

/// <summary>The reader's own log on one boulder: the marks the boulder card and detail page show them.</summary>
/// <param name="BoulderId">The boulder.</param>
/// <param name="Attempts">How many entries the reader logged (attempts, sends and flashes alike).</param>
/// <param name="Sent">True when one of them is a send.</param>
/// <param name="Flashed">True when one of them is a flash.</param>
/// <param name="LastAt">The newest entry's time.</param>
public sealed record MineDocumentTick(Guid BoulderId, int Attempts, bool Sent, bool Flashed, DateTimeOffset LastAt);
