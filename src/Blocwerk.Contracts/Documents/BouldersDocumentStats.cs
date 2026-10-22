// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>A boulder's wall-wide counts: the same for every reader, never the reader's own marks.</summary>
/// <param name="Ascents">How many climbers sent or flashed it.</param>
/// <param name="RatingCount">How many ratings it has.</param>
/// <param name="RatingAverage">The mean rating, two decimals, or null without ratings.</param>
public sealed record BouldersDocumentStats(int Ascents, int RatingCount, double? RatingAverage);
