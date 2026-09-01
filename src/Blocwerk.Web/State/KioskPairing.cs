namespace Blocwerk.Web.State;

/// <summary>
/// Where a device pairing has got to. There is no "Rejected": an admin who does not approve simply
/// lets it expire, and an expired pairing is indistinguishable from one that never existed.
/// </summary>
public enum KioskPairingStatus
{
    /// <summary>The tablet is showing its code and waiting. No wall, no key, no authority.</summary>
    Pending = 0,

    /// <summary>
    /// A wall admin picked a wall and a kiosk key was minted for it. The entry is now
    /// CREDENTIAL-EQUIVALENT — whoever redeems it becomes that wall's tablet — so from here it is
    /// only ever reachable with the claim ticket, exactly once.
    /// </summary>
    Approved = 1,

    /// <summary>The tablet redeemed it and the entry was removed. A terminal value, never stored.</summary>
    Redeemed = 2,
}

/// <summary>
/// A pairing as the REGISTRY holds it, claim ticket included. Handed out only by
/// <see cref="KioskPairingRegistry.Create"/>, to the one circuit that created it.
/// </summary>
/// <param name="Id">Identifies the pairing. Not a secret, and not guessable either.</param>
/// <param name="Code">The six digits a human reads off the tablet. Unique among ACTIVE pairings.</param>
/// <param name="ClaimTicket">
/// The tablet's proof that it is the device that asked for this code. Never rendered where a camera
/// can see it, never handed to an approving admin, and required to redeem.
/// </param>
/// <param name="CreatedAt">When the tablet asked.</param>
/// <param name="ExpiresAt">When it stops being anything at all.</param>
public sealed record KioskPairingEntry(
    Guid Id,
    string Code,
    string ClaimTicket,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A pairing as anyone LOOKING ONE UP sees it: no claim ticket. The approval paths get this, so
/// there is no code path on which typing a six-digit code can yield the ticket that redeems it.
/// </summary>
/// <param name="Id">The pairing's id.</param>
/// <param name="Code">The six digits.</param>
/// <param name="Status">Pending or Approved.</param>
/// <param name="ExpiresAt">When it lapses.</param>
/// <param name="WallId">The wall an admin chose, once approved.</param>
/// <param name="ApiKeyId">The kiosk key minted for that wall, once approved.</param>
public sealed record KioskPairingState(
    Guid Id,
    string Code,
    KioskPairingStatus Status,
    DateTimeOffset ExpiresAt,
    Guid? WallId,
    Guid? ApiKeyId);

/// <summary>
/// What a successful redemption yields: the two values <c>KioskDeviceCookie.Write</c> needs, and
/// nothing else. The plaintext token was discarded at minting time and never existed here.
/// </summary>
/// <param name="ApiKeyId">The minted kiosk key's id.</param>
/// <param name="WallId">The wall it belongs to.</param>
public sealed record KioskPairingRedemption(Guid ApiKeyId, Guid WallId);
