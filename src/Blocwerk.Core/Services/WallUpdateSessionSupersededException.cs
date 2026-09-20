namespace Blocwerk.Core.Services;

/// <summary>
/// Thrown when a caller tries to promote or discard a big-wall update that is no longer the wall's
/// in-flight one — its session was taken over, discarded or already promoted while the caller's browser
/// sat on it.
/// <para>
/// This is the guard against the worst failure the flow had: an admin parked on the Confirm step, whose
/// staged panels were replaced by a second admin's takeover, pressing Apply and promoting the SECOND
/// admin's photos with the FIRST admin's (now meaningless) decisions and stale warp geometry. Identity
/// of the session is therefore part of the promote/discard contract, not something inferred from the
/// wall alone.
/// </para>
/// </summary>
public class WallUpdateSessionSupersededException : InvalidOperationException
{
    public WallUpdateSessionSupersededException(Guid wallId, Guid expectedSessionId, Guid? currentSessionId)
        : base("This wall update was replaced by a newer one, so it can no longer be applied or discarded. "
            + "Reload the wall to see the update that is in progress now.")
    {
        WallId = wallId;
        ExpectedSessionId = expectedSessionId;
        CurrentSessionId = currentSessionId;
    }

    /// <summary>The wall the refused operation targeted.</summary>
    public Guid WallId { get; }

    /// <summary>The session the caller believed it was working on.</summary>
    public Guid ExpectedSessionId { get; }

    /// <summary>The wall's actually-open session, or null when no update is open on it any more.</summary>
    public Guid? CurrentSessionId { get; }
}
