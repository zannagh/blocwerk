using Blocwerk.Core.Abstractions;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Guards the offline queue's replay against wrong-user attribution.
/// <para>
/// A queued action carries no identity of its own: it is replayed as a plain cookie-authenticated
/// POST, and the Core services resolve who did it from <see cref="ICurrentUserService"/> at REPLAY
/// time. On a device where one browser profile is used by several people in sequence — a kiosk
/// tablet acting as a member, a shared laptop — the person authenticated at replay time is not
/// necessarily the person who tapped. A send queued by A while the network was flaky, replayed
/// after A released the tablet and B picked themselves, would be recorded as B's send.
/// </para>
/// <para>
/// So the client stamps every entry with the acting user's id at ENQUEUE time and sends it back as
/// <c>queuedForUserId</c>. This type is the server-side half: the replay only proceeds when the
/// stamp matches whoever the request is actually authenticated as. A mismatch is answered with 409
/// so the client can KEEP the entry queued (it may still replay correctly when its owner returns)
/// rather than drop it or write it under the wrong name.
/// </para>
/// <para>
/// The stamp is not a credential and grants nothing: it can only ever narrow what a request is
/// allowed to do, never widen it. Every existing authorization check still runs afterwards.
/// </para>
/// </summary>
public static class OfflineActionOwnership
{
    /// <summary>
    /// Shown to the person now signed in, so a held queue is explicable rather than mysterious.
    /// </summary>
    public const string MismatchMessage =
        "This queued action belongs to a different account. It stays queued until they sign in again.";

    /// <summary>
    /// True when the queued action may be attributed to the caller: either it carries no stamp
    /// (queued by a build that predates stamping — see the client's migration note) or the stamp is
    /// the caller's own id.
    /// </summary>
    /// <param name="currentUser">Resolves the identity the write would be attributed to. Throws
    /// <see cref="UnauthorizedAccessException"/> when there is none, which the callers map to 401.</param>
    /// <param name="queuedForUserId">The id stamped on the entry at enqueue time, if any.</param>
    public static async Task<bool> MatchesCallerAsync(
        ICurrentUserService currentUser,
        Guid? queuedForUserId)
    {
        if (queuedForUserId is not { } stamped || stamped == Guid.Empty)
        {
            return true;
        }

        var user = await currentUser.GetCurrentUserAsync();
        return user.Id == stamped;
    }
}
