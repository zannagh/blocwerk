namespace Blocwerk.Core.Enums;

/// <summary>The kind of row mutation a <see cref="Entities.ChangeJournalEntry"/> records.</summary>
public enum ChangeJournalOp
{
    /// <summary>A new row was inserted; only the after-image is recorded.</summary>
    Insert = 0,

    /// <summary>An existing row changed; both the before-image and the changed properties are recorded.</summary>
    Update = 1,

    /// <summary>A row was deleted; only the before-image is recorded.</summary>
    Delete = 2,
}
