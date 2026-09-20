namespace Blocwerk.Core.Enums;

/// <summary>
/// What <see cref="Data.HoldDeletion"/> does with the <see cref="Entities.BoulderHold"/> rows that
/// point at a hold about to be deleted. The FK is Restrict, so a hold with memberships cannot be
/// removed while they exist — the choice is between dropping them deliberately and letting the
/// delete fail loudly.
/// </summary>
public enum HoldDeleteBoulderPolicy
{
    /// <summary>
    /// Drop the memberships and flag every active boulder that used the hold historic. The explicit
    /// "this hold is gone from the wall" paths.
    /// </summary>
    DetachAndFlagHistoric = 0,

    /// <summary>
    /// Leave the memberships alone. For paths whose hold set is already boulder-free by construction
    /// (staged rows, unreferenced auto-detections): if one ever is not, the Restrict FK still stops
    /// the delete rather than silently orphaning a boulder.
    /// </summary>
    LeaveUntouched = 1,
}
