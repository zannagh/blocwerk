using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Blocwerk.Web.State;

/// <summary>
/// In-memory, app-wide store of device pairings in flight: a tablet shows a six-digit code, a wall
/// admin approves it against one of their walls, and the tablet redeems the result exactly once to
/// become that wall's kiosk.
/// </summary>
/// <remarks>
/// <b>A pairing carries no wall until an admin puts one there.</b> The anonymous tablet that calls
/// <see cref="Create"/> cannot nominate a wall, and there is no method that lets it; the wall arrives
/// only through <see cref="TryApprove"/>, from a caller that has already been checked against that
/// wall's admin list. So the worst an unauthenticated device can do on its own is occupy one of the
/// six-digit codes for three minutes.
/// <para>
/// <b>Two secrets, deliberately asymmetric.</b> The CODE is short because a human retypes it, and is
/// therefore treated as public — anybody in the room can read it off the screen, and the throttles in
/// <see cref="KioskThrottleRegistry"/> are what stop it being ground down. The CLAIM TICKET is 256
/// bits and never leaves the tablet's circuit; it is what makes the code safe to display. Once
/// approved, the entry is credential-equivalent (redeeming it makes you the wall's tablet), so
/// redemption demands the ticket, happens under a lock, and removes the entry — a bystander who
/// photographed the screen cannot race the real tablet for the key that was just minted.
/// </para>
/// <para>
/// Singleton, mirroring <see cref="KioskThrottleRegistry"/> and <see cref="EditActivityRegistry"/>:
/// the tablet's circuit, the admin's circuit and the completion HTTP request are three different
/// scopes that must see the same entry. Losing the map on restart is fine for the same reason it is
/// there: a pairing lives three minutes, and the worst case is a tablet showing a code nobody can
/// approve, which the admin resolves by tapping "get a new code".
/// </para>
/// <para>
/// There is no sweeper. Expiry is checked ON READ — an entry past its <c>ExpiresAt</c> is invisible
/// to every lookup whether or not it has been removed — and stale entries are pruned ON WRITE, the
/// same shape <see cref="KioskThrottleRegistry.IsLocked(KioskThrottleScope, DateTimeOffset?)"/> and
/// <c>Prune</c> use. Blocwerk.Web has no background-service infrastructure and this is not the
/// feature that should introduce one.
/// </para>
/// </remarks>
public sealed class KioskPairingRegistry
{
    /// <summary>
    /// How long a code is worth anything. Long enough to walk across the gym with a phone, short
    /// enough that an abandoned code on a screen is not a standing invitation.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How many pairings may be waiting at once, across the whole installation.
    /// </summary>
    /// <remarks>
    /// <b>A cap on CONCURRENCY, not on the rate of creation.</b> The rate version of this was a
    /// standing denial of service: it counted every successful creation against one global,
    /// exponentially-backing-off counter that nothing ever reset, so a handful of tablets rebooting
    /// — or one anonymous request every half hour — locked every tablet in the installation out of
    /// pairing indefinitely. Nothing is being GUESSED when a code is created, so there was never
    /// anything for a backoff to bound.
    /// <para>
    /// A concurrency cap says the thing actually worth saying: at most this many of the million
    /// codes are held open at one time. It cannot accumulate, because it is a measure of the present
    /// — every entry releases itself within <see cref="Lifetime"/>, and a tablet that walks away
    /// releases its own on dispose. A fleet of tablets rebooting together holds one entry each and
    /// re-pairing releases the old entry before taking a new one, so normal use cannot reach it.
    /// </para>
    /// </remarks>
    public const int MaxLivePairings = 200;

    /// <summary>
    /// How many times a colliding six-digit draw is retried before creation gives up. With
    /// <see cref="MaxLivePairings"/> keeping the live set far below the 10^6 space, a single
    /// collision is already improbable and thirty in a row cannot happen; the bound exists so a bug
    /// can never spin here forever.
    /// </summary>
    private const int MaxCodeAttempts = 30;

    private readonly ConcurrentDictionary<Guid, StoredPairing> pairings = new();

    /// <summary>
    /// Serialises the read-modify-write transitions. <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// makes each operation atomic but not each SEQUENCE, and every interesting step here is a
    /// sequence: "is this code taken, if not take it", and above all "is this still approved and
    /// unredeemed, if so remove it and hand back the key". The second one is the single-use
    /// guarantee, and a compare-and-swap spread over two dictionary calls would not provide it.
    /// </summary>
    private readonly object gate = new();

    /// <summary>
    /// Raised with a pairing's id whenever it changes, so the tablet's circuit learns that an admin
    /// (on some other circuit, maybe on another device entirely) approved it.
    /// </summary>
    /// <remarks>
    /// Same shape and same defensiveness as <c>DomainChangeNotifier</c>: invoked over
    /// <c>GetInvocationList</c> with exceptions swallowed, because one stale circuit's handler
    /// throwing must not fail the admin's approval or starve the tablet that is actually waiting.
    /// Not <c>IDomainChangeNotifier</c> itself — that carries a fixed EF-shaped payload and this is
    /// not a database change.
    /// </remarks>
    public event Action<Guid>? Changed;

    /// <summary>
    /// Opens a pairing: a fresh id, a six-digit code unique among the ACTIVE pairings, and a claim
    /// ticket for the calling circuit to hold on to.
    /// </summary>
    /// <remarks>
    /// Uniqueness among active entries is what makes the typed path safe to build at all: a code an
    /// admin types can only ever resolve to one waiting tablet, so there is no way to approve the
    /// wrong device by collision. Expired entries do NOT reserve their code — they are invisible to
    /// every lookup, so re-issuing one is not a collision in any sense that matters.
    /// </remarks>
    /// <returns>
    /// The new pairing, or null when the installation is already holding
    /// <see cref="MaxLivePairings"/> of them or no free code could be drawn. Null rather than an
    /// exception because the caller is a page in a live circuit: there is a screen to put "try again
    /// in a moment" on, and an unhandled exception there kills the circuit instead.
    /// </returns>
    public KioskPairingEntry? Create(DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var ticket = GenerateClaimTicket();

        lock (gate)
        {
            Prune(now);

            // Counted inside the lock and after the prune, so the number is the live one and two
            // tablets arriving together cannot both squeeze past the last slot.
            if (CountActive(now) >= MaxLivePairings)
            {
                return null;
            }

            for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
            {
                var code = GenerateCode();
                if (FindActiveByCode(code, now) is not null)
                {
                    continue;
                }

                var stored = new StoredPairing(
                    Guid.NewGuid(),
                    code,
                    ticket,
                    now,
                    now.Add(Lifetime),
                    KioskPairingStatus.Pending,
                    WallId: null,
                    ApiKeyId: null);

                pairings[stored.Id] = stored;
                return new KioskPairingEntry(stored.Id, stored.Code, stored.ClaimTicket, stored.CreatedAt, stored.ExpiresAt);
            }
        }

        return null;
    }

    /// <summary>The pairing with this id, or null when it is unknown or has expired.</summary>
    public KioskPairingState? Find(Guid pairingId, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;

        // Under the gate, like every other read-modify-write here. Removing the expired entry
        // outside it raced TryApprove's write and could resurrect an entry that had just been
        // removed — harmless in itself, since the resurrected entry is expired and invisible to
        // every lookup, but it broke the invariant the rest of the class is built on.
        lock (gate)
        {
            if (!pairings.TryGetValue(pairingId, out var stored))
            {
                return null;
            }

            // Check-on-read: an entry past its lifetime is gone as far as every caller is concerned,
            // whether or not a write has come along to prune it yet.
            if (stored.ExpiresAt <= now)
            {
                pairings.TryRemove(pairingId, out _);
                return null;
            }

            return stored.ToState();
        }
    }

    /// <summary>
    /// The ACTIVE pairing showing this code, or null. Never returns the claim ticket: the typed
    /// approval path knows the code and must not thereby learn the secret that redeems it.
    /// </summary>
    public KioskPairingState? TryFindByCode(string? code, DateTimeOffset? nowOverride = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var now = nowOverride ?? DateTimeOffset.UtcNow;

        lock (gate)
        {
            return FindActiveByCode(code.Trim(), now)?.ToState();
        }
    }

    /// <summary>
    /// Records an admin's decision: this pairing is for that wall, with that freshly minted kiosk
    /// key. Returns false when the pairing is unknown, expired, or already approved.
    /// </summary>
    /// <remarks>
    /// Approving twice is refused rather than overwritten. A second approval would strand the first
    /// minted key — a live kiosk credential for a wall, attached to nothing, that nobody would think
    /// to revoke. The caller mints only after this returns true.
    /// </remarks>
    public bool TryApprove(Guid pairingId, Guid wallId, Guid apiKeyId, DateTimeOffset? nowOverride = null)
    {
        if (wallId == Guid.Empty || apiKeyId == Guid.Empty)
        {
            return false;
        }

        var now = nowOverride ?? DateTimeOffset.UtcNow;

        lock (gate)
        {
            if (!pairings.TryGetValue(pairingId, out var stored)
                || stored.ExpiresAt <= now
                || stored.Status != KioskPairingStatus.Pending)
            {
                return false;
            }

            pairings[pairingId] = stored with
            {
                Status = KioskPairingStatus.Approved,
                WallId = wallId,
                ApiKeyId = apiKeyId,
            };
        }

        RaiseChanged(pairingId);
        return true;
    }

    /// <summary>
    /// Consumes an approved pairing: verifies the claim ticket, removes the entry, and hands back
    /// the key and wall for the device cookie. Null for anything else — unknown, expired, still
    /// pending, wrong ticket, or already redeemed.
    /// </summary>
    /// <remarks>
    /// The whole check-and-remove runs inside the lock, so two requests arriving together cannot
    /// both come away with the key: exactly one finds the entry, and the other finds nothing. The
    /// ticket comparison is constant-time — the entry is a credential at this point, and an
    /// early-exit compare on a value an attacker can submit repeatedly is a needless oracle.
    /// </remarks>
    public KioskPairingRedemption? TryRedeem(Guid pairingId, string? claimTicket, DateTimeOffset? nowOverride = null)
    {
        if (string.IsNullOrEmpty(claimTicket))
        {
            return null;
        }

        var now = nowOverride ?? DateTimeOffset.UtcNow;

        lock (gate)
        {
            if (!pairings.TryGetValue(pairingId, out var stored)
                || stored.ExpiresAt <= now
                || stored.Status != KioskPairingStatus.Approved
                || stored.WallId is not { } wallId
                || stored.ApiKeyId is not { } apiKeyId)
            {
                return null;
            }

            if (!TicketsMatch(stored.ClaimTicket, claimTicket))
            {
                // Deliberately NOT removed. A wrong ticket is somebody else guessing, and letting a
                // guess destroy the pairing would hand them a denial of service against the tablet
                // that is legitimately waiting. The guess throttle is what bounds the retries.
                return null;
            }

            pairings.TryRemove(pairingId, out _);
            Prune(now);
            return new KioskPairingRedemption(apiKeyId, wallId);
        }
    }

    /// <summary>Drops a pairing, e.g. when the tablet asks for a fresh code.</summary>
    public void Remove(Guid pairingId)
    {
        pairings.TryRemove(pairingId, out _);
    }

    /// <summary>Active pairings right now. For the concurrency cap and for tests.</summary>
    public int ActiveCount(DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;

        lock (gate)
        {
            return CountActive(now);
        }
    }

    /// <summary>Caller holds <see cref="gate"/>.</summary>
    private int CountActive(DateTimeOffset now)
    {
        return pairings.Count(pair => pair.Value.ExpiresAt > now);
    }

    /// <summary>Six digits, uniformly drawn, leading zeros kept so every code is the same length.</summary>
    private static string GenerateCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    /// <summary>256 bits, URL-safe. Long enough that guessing it is not a strategy.</summary>
    private static string GenerateClaimTicket()
    {
        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    }

    private static bool TicketsMatch(string expected, string supplied)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(supplied));
    }

    /// <summary>Caller holds <see cref="gate"/>.</summary>
    private StoredPairing? FindActiveByCode(string code, DateTimeOffset now)
    {
        return pairings
            .Where(pair => pair.Value.ExpiresAt > now && string.Equals(pair.Value.Code, code, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// Drops expired entries. Called on every write. The creation cap keeps the map in the tens and
    /// an entry lives three minutes, so a full pass is cheaper than deciding whether to do one — and
    /// unconditional means there is no threshold to be wrong about.
    /// </summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in pairings.Where(pair => pair.Value.ExpiresAt <= now))
        {
            pairings.TryRemove(pair.Key, out _);
        }
    }

    private void RaiseChanged(Guid pairingId)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<Guid>)handler)(pairingId);
            }
            catch
            {
                // A circuit that has gone away, or whose handler threw, must not fail the admin's
                // approval or stop the tablet that is actually waiting from being told.
            }
        }
    }

    /// <summary>
    /// The stored shape. Private because it carries the claim ticket: callers get either
    /// <see cref="KioskPairingEntry"/> (creation, once) or <see cref="KioskPairingState"/> (lookups).
    /// </summary>
    private sealed record StoredPairing(
        Guid Id,
        string Code,
        string ClaimTicket,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        KioskPairingStatus Status,
        Guid? WallId,
        Guid? ApiKeyId)
    {
        public KioskPairingState ToState()
        {
            return new KioskPairingState(Id, Code, Status, ExpiresAt, WallId, ApiKeyId);
        }
    }
}
