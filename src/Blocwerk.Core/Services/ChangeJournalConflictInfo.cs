namespace Blocwerk.Core.Services;

/// <summary>
/// A <see cref="ChangeJournalConflict"/> with its raw <see cref="ChangeJournalConflict.KeyJson"/>
/// resolved far enough for a human to read. The UI should render <see cref="Description"/> (and the
/// conflict's <see cref="ChangeJournalConflict.Reason"/>) — never the key JSON.
/// </summary>
/// <param name="Conflict">The underlying conflict, unchanged.</param>
/// <param name="ShortKey">The key's values shortened for display, e.g. <c>Id=4f2a1b9c</c>.</param>
/// <param name="EntityName">
/// The row's human name when it was cheap to resolve — today only walls and boulders have one. Null
/// for everything else (a hold has no name); resolving a hold to "hold 12 on Nordwand" needs wall/panel
/// joins that belong in the Web layer, which has the batch's scope name to work with already.
/// </param>
public sealed record ChangeJournalConflictInfo(
    ChangeJournalConflict Conflict,
    string ShortKey,
    string? EntityName)
{
    /// <summary>One line naming the row the conflict is about, e.g. <c>Wall "Nordwand" (Id=4f2a1b9c)</c>.</summary>
    public string Description => EntityName is null
        ? $"{Conflict.EntityType} ({ShortKey})"
        : $"{Conflict.EntityType} \"{EntityName}\" ({ShortKey})";
}
