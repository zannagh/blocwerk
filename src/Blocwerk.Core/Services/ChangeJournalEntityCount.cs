using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// How many rows of one entity type a batch touched with one operation, e.g.
/// <c>(Hold, Delete, 181)</c>. Produced by a GROUP BY over the journal entries, never by loading them.
/// </summary>
public sealed record ChangeJournalEntityCount(string EntityType, ChangeJournalOp Op, int Count);
