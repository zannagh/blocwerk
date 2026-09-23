namespace Blocwerk.Core.Enums;

/// <summary>
/// Where a whole-wall big update has got to. Persisted on <see cref="Entities.WallUpdateSession"/> as
/// the resume cursor, so closing the browser mid-flow returns to the step the user left rather than to
/// the upload form. Also drives the wizard's own view switch (Blocwerk.Web BigWallUpdate), which is why
/// the transient, never-persisted view states (<see cref="Loading"/>, <see cref="Working"/>) are members
/// too: the cursor is only ever written at a phase the user can actually be resumed into.
/// <para>
/// THE NUMBERS ARE PERSISTED. <c>WallUpdateSession.Phase</c> is stored as the underlying integer, so a
/// stored cursor means whatever member carries that number. Every member therefore has an EXPLICIT
/// assignment: a new phase must take the next free number at the END, never be inserted in the middle,
/// or every already-stored cursor silently re-points at a different step.
/// </para>
/// </summary>
public enum WallUpdatePhase
{
    /// <summary>Transient: the wizard is probing for an in-flight update. Never persisted.</summary>
    Loading = 0,

    /// <summary>An in-flight update was found; the user is being offered resume-or-discard.</summary>
    ResumePrompt = 1,

    /// <summary>Awaiting the fresh multi-photo capture. The state before a session exists.</summary>
    Upload = 2,

    /// <summary>Pre-match review of the raw detections on the staged panels.</summary>
    Detected = 3,

    /// <summary>The old-hold carryover review against the staged centre.</summary>
    Carryover = 4,

    /// <summary>The per-neighbour overlap walk; the position in it is the session's neighbour index.</summary>
    Neighbours = 5,

    /// <summary>Post-match manual touch-up of the staged holds.</summary>
    Touchup = 6,

    /// <summary>Everything decided; awaiting the final Apply.</summary>
    Confirm = 7,

    /// <summary>Transient: a service call is in flight. Never persisted.</summary>
    Working = 8,

    /// <summary>The update was applied.</summary>
    Done = 9,

    /// <summary>The optional "recognise hold shapes" step, after the final touch-up.</summary>
    Shapes = 10,

    /// <summary>Reviewing the recognised shapes, lowest confidence first, before the confirm step.</summary>
    ShapeReview = 11,
}
