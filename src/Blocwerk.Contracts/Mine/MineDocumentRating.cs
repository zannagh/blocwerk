// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Mine;

/// <summary>The reader's own star rating of one boulder, as the boulder page's rating control shows it.</summary>
/// <param name="BoulderId">The boulder.</param>
/// <param name="Stars">Whole stars, 1 to 5.</param>
public sealed record MineDocumentRating(Guid BoulderId, int Stars);
