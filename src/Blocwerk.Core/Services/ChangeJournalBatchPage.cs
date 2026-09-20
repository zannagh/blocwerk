namespace Blocwerk.Core.Services;

/// <summary>
/// One page of <see cref="ChangeJournalBatchSummary"/>, newest first. There is no total count on
/// purpose: the journal tables have no retention job, so counting them all is exactly the query that
/// gets slower forever. Page with <see cref="Skip"/>/<see cref="Take"/> and drive "next" off
/// <see cref="HasMore"/>.
/// </summary>
public sealed record ChangeJournalBatchPage(
    IReadOnlyList<ChangeJournalBatchSummary> Items,
    int Skip,
    int Take,
    bool HasMore);
