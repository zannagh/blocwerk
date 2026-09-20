namespace Blocwerk.Core.Enums;

/// <summary>The lifecycle state of a <see cref="Entities.WallUpdateSession"/>.</summary>
public enum WallUpdateSessionStatus
{
    /// <summary>In flight: staged panels exist and the decisions may still change. At most one per wall.</summary>
    Open = 0,

    /// <summary>The update was applied to the live wall; the session is history.</summary>
    Promoted = 1,

    /// <summary>
    /// The update was abandoned — either explicitly (Discard) or by a later admin taking the wall over.
    /// Its staged panels and holds are gone and its decisions are meaningless.
    /// </summary>
    Discarded = 2,
}
