namespace Blocwerk.Core.Enums;

/// <summary>The lifecycle state of a <see cref="Entities.ChangeJournalBatch"/>.</summary>
public enum ChangeJournalStatus
{
    /// <summary>The batch was captured and is the authoritative record of an applied change.</summary>
    Recorded = 0,

    /// <summary>The batch's effect has since been reverted (a later phase sets this).</summary>
    Reverted = 1,

    /// <summary>The batch has been replayed onto another environment (a later phase sets this).</summary>
    Replayed = 2,

    /// <summary>An audited action whose outcome is not known yet: written before the action runs, so none goes unrecorded.</summary>
    Pending = 3,

    /// <summary>An audited action that failed after journalling some rows; kept so those rows stay explained.</summary>
    Failed = 4,
}
