namespace Blocwerk.Web.State;

/// <summary>
/// The kind of unsaved, in-flight editing work that makes the app "busy" for the deploy gate.
/// </summary>
public enum EditKind
{
    /// <summary>A user has the boulder-create page open (a live create session).</summary>
    BoulderCreate,

    /// <summary>A user has the boulder-revise page open (unsaved hold/rule edits).</summary>
    BoulderRevise,

    /// <summary>A user has an inline boulder editor open (name/grade, grade proposal, backdate).</summary>
    BoulderEdit,

    /// <summary>A user has the wall-create page open (an unsaved new-wall draft).</summary>
    WallCreate,

    /// <summary>A user has a wall in edit/alignment mode.</summary>
    WallEdit,
}
