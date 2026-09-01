using System.Collections.Concurrent;

namespace Blocwerk.Web.State;

/// <summary>
/// In-memory, app-wide brute-force throttle for the two kiosk endpoints that take a secret: the
/// registration key and a member's kiosk PIN.
/// </summary>
/// <remarks>
/// Deliberately NOT <c>ILoginLockoutService</c>. That one records failures on the USER row
/// (<c>FailedAuthCount</c>/<c>LockoutUntil</c>), which would let anybody standing at a public tablet
/// lock a member out of their real account by mistyping a 4-digit PIN five times — a denial of
/// service handed to every passer-by. The counter therefore lives in memory only and nothing about
/// it touches the database.
/// <para>
/// <b>Two properties do the work.</b> First, the lockout is EXPONENTIAL in the number of consecutive
/// failure bursts, not a flat minute: a flat minute after five tries is 7,200 guesses a day against
/// a four-digit PIN, which is most of the space, and the whole point of keeping this out of the user
/// row is that nobody is ever notified that it is happening. Second, every attempt is counted
/// against SEVERAL scopes at once (see <see cref="KioskThrottleScope"/>): a per-target counter, and
/// a per-device counter that a round-robin across every consenting member cannot escape.
/// </para>
/// <para>
/// Singleton so it spans every circuit and request on this instance, mirroring
/// <see cref="EditActivityRegistry"/>. Losing it on restart is acceptable: it caps online guessing,
/// and the PIN is only ever one factor on a device that already holds a valid kiosk key.
/// </para>
/// </remarks>
public sealed class KioskThrottleRegistry
{
    /// <summary>Failures tolerated inside a scope's window before it is locked out.</summary>
    public const int MaxAttempts = 5;

    /// <summary>How long failures accumulate for.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>The FIRST lockout. Each further consecutive burst doubles it.</summary>
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The ceiling on the doubling. Long enough to make sustained guessing pointless, short enough
    /// that a member who genuinely forgot their PIN is not locked out for the evening.
    /// </summary>
    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the escalation is remembered after the last failure. Without this, waiting out one
    /// lockout would reset the doubling and the backoff would never actually bite; with it, an
    /// attacker has to go quiet for an hour to get back to a one-minute lockout, and an honest
    /// person who fumbles a PIN once a week never notices it at all.
    /// </summary>
    public static readonly TimeSpan BurstMemory = TimeSpan.FromHours(1);

    /// <summary>
    /// Entries are kept for <see cref="BurstMemory"/> after their last failure, so the map is pruned
    /// on write once it grows past this. Only expired entries are dropped.
    /// </summary>
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Entry> entries = new();

    /// <summary>
    /// The two scopes a PIN attempt counts against: the targeted member, and the DEVICE across every
    /// member. Without the second, five guesses per member times every consenting member on the wall
    /// is a much larger budget than five, for the same 10,000-value space.
    /// </summary>
    /// <remarks>
    /// The device scope keys on the registration's <c>DeviceId</c>, NOT on the kiosk key: a wall may
    /// register several tablets with the SAME key, and keying on the key would put them all in one
    /// bucket — somebody guessing at the tablet by the door would lock out the one upstairs. The id
    /// is random per registration and comes off the device cookie the caller already validated, so
    /// the cap it enforces — one device cannot buy more guesses by walking the member picker — is
    /// unchanged.
    /// </remarks>
    public static IReadOnlyList<KioskThrottleScope> PinScopes(Guid apiKeyId, Guid deviceId, Guid targetUserId)
    {
        return
        [
            new KioskThrottleScope($"pin:{apiKeyId:N}:{targetUserId:N}", MaxAttempts, Window, Lockout),

            // Deliberately looser per attempt and harsher per lockout: a member re-typing their own
            // PIN must not trip it, while a device working through the picker hits it quickly.
            new KioskThrottleScope($"pindev:{deviceId:N}", 15, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5)),
        ];
    }

    /// <summary>
    /// The two scopes a registration attempt counts against.
    /// </summary>
    /// <remarks>
    /// The client address is NOT trustworthy here and the second scope is what makes that survivable.
    /// The app honours <c>X-Forwarded-For</c> from any client (<c>KnownProxies</c>/
    /// <c>KnownIPNetworks</c> are both cleared, app-wide, so the real client address is visible behind
    /// the reverse proxy), which cuts both ways: an attacker rotates the header and never trips the
    /// per-address counter, and a spoofed header carrying the PROXY's address locks out every
    /// legitimate device, since that is the address all real traffic appears to come from. So the
    /// per-address scope is kept only because it slows a naive attacker, and the GLOBAL scope — which
    /// keys on nothing the client can influence — is the actual cap. Its budget is sized for the fact
    /// that registration is a rare, deliberate act by a wall admin standing at the tablet.
    /// </remarks>
    public static IReadOnlyList<KioskThrottleScope> RegistrationScopes(string? clientAddress)
    {
        return
        [
            new KioskThrottleScope($"reg:{clientAddress ?? "unknown"}", MaxAttempts, Window, Lockout),
            new KioskThrottleScope("reg:global", 20, TimeSpan.FromMinutes(10), Lockout),
        ];
    }

    // There is deliberately NO scope here for CREATING a pairing code, and this registry is the
    // wrong tool for that job. Nothing is guessed when a tablet asks for a code, so there is nothing
    // for an exponential backoff to bound — while the damage of getting it wrong is total, because
    // the only scope available is a global one (a pairing is created inside the tablet's circuit,
    // where there is no client address to key on) and a global counter that escalates and is never
    // reset locks every tablet in the installation out at the rate of one request per lockout. That
    // is exactly what it used to do. The cap that belongs there is on how many pairings are LIVE at
    // once, which cannot accumulate and expires by itself: KioskPairingRegistry.MaxLivePairings.

    /// <summary>
    /// The scopes a TYPED six-digit pairing code counts against, in the wall-settings card. The
    /// space is 10^6 — small enough that an unbounded form field would be a real attack — and this
    /// is what bounds it.
    /// </summary>
    /// <remarks>
    /// The per-USER scope is the cap that actually holds, and it is stronger than anything the
    /// registration path can manage: typing a code requires being signed in, so the key is an
    /// authenticated user id rather than a header the caller writes for themselves. A determined
    /// guesser needs an account per bucket, and every bucket they burn is attributable.
    /// <para>
    /// The per-CODE scope covers the other shape: not "find any pairing" but "grind at the ONE code
    /// I saw on the screen across the gym". It keys on the guessed code, which the caller does
    /// control, so it cannot bound a guesser walking the space — that is the per-user scope's job —
    /// but it does stop a specific waiting tablet being targeted, and it costs nothing.
    /// </para>
    /// <para>
    /// The per-code scope is FLAT, and the per-user one is not. Its key is attacker-controlled —
    /// anybody who can read the six digits off the tablet can spend that code's five attempts — so
    /// letting it escalate would hand a bystander a half-hour denial of service against the tablet
    /// legitimately waiting, from a code they were always able to see. A flat minute still stops the
    /// grinding it exists to stop, and a code only lives three of them.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<KioskThrottleScope> PairingGuessScopes(Guid actingUserId, string? code)
    {
        return
        [
            new KioskThrottleScope($"pairguess:{actingUserId:N}", MaxAttempts, Window, Lockout),
            new KioskThrottleScope($"pairguess:code:{code ?? "empty"}", MaxAttempts, Window, Lockout, Flat: true),
        ];
    }

    /// <summary>True while ANY of the scopes is locked out and the secret must not be checked.</summary>
    public bool IsLocked(IReadOnlyList<KioskThrottleScope> scopes, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;

        foreach (var scope in scopes)
        {
            if (IsLocked(scope, now))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True while this scope is locked out and must be refused without checking the secret.</summary>
    public bool IsLocked(KioskThrottleScope scope, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        if (!entries.TryGetValue(scope.Key, out var entry))
        {
            return false;
        }

        if (entry.LockedUntil > now)
        {
            return true;
        }

        // Not locked. The entry is kept anyway until the escalation is forgotten — dropping it the
        // moment the lockout lapsed is what would let an attacker reset the doubling by waiting.
        if (now - entry.LastFailure > BurstMemory)
        {
            entries.TryRemove(scope.Key, out _);
        }

        return false;
    }

    /// <summary>Records one failed attempt against every scope it counts towards.</summary>
    public void RegisterFailure(IReadOnlyList<KioskThrottleScope> scopes, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;

        foreach (var scope in scopes)
        {
            RegisterFailure(scope, now);
        }

        Prune(now);
    }

    /// <summary>Records a failed attempt, locking the scope out once its cap is reached.</summary>
    public void RegisterFailure(KioskThrottleScope scope, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;

        entries.AddOrUpdate(
            scope.Key,
            _ => new Entry(1, now, DateTimeOffset.MinValue, Bursts: 0, LastFailure: now),
            (_, existing) =>
            {
                // Quiet for long enough that the escalation is forgiven: start over completely.
                if (now - existing.LastFailure > BurstMemory)
                {
                    return new Entry(1, now, DateTimeOffset.MinValue, Bursts: 0, LastFailure: now);
                }

                // A failure outside the counting window starts a fresh count — but KEEPS the burst
                // count, which is what makes the next lockout longer than the last one.
                if (now - existing.FirstFailure > scope.Window)
                {
                    return existing with { Failures = 1, FirstFailure = now, LastFailure = now };
                }

                var count = existing.Failures + 1;
                if (count < scope.MaxAttempts)
                {
                    return existing with { Failures = count, LastFailure = now };
                }

                // Already serving a lockout: keep counting, but do NOT escalate again. A burst is
                // one burst however many further attempts arrive while it is locked — otherwise a
                // caller that reports failures without checking IsLocked first would drive the
                // backoff straight to its ceiling on the first burst.
                if (existing.LockedUntil > now)
                {
                    return existing with { Failures = count, LastFailure = now };
                }

                var bursts = existing.Bursts + 1;
                return existing with
                {
                    Failures = count,
                    LockedUntil = now.Add(BackoffFor(scope, bursts)),
                    Bursts = bursts,
                    LastFailure = now,
                };
            });
    }

    /// <summary>Clears the counters after a success.</summary>
    public void Reset(IReadOnlyList<KioskThrottleScope> scopes)
    {
        foreach (var scope in scopes)
        {
            entries.TryRemove(scope.Key, out _);
        }
    }

    /// <summary>
    /// The lockout for the <paramref name="bursts"/>-th consecutive burst: the scope's base lockout
    /// doubled once per burst, capped at <see cref="MaxLockout"/>.
    /// </summary>
    public static TimeSpan BackoffFor(KioskThrottleScope scope, int bursts)
    {
        // A flat scope never escalates: see PairingGuessScopes for why a scope keyed on something
        // the caller controls must not be able to lock anything out for half an hour.
        if (bursts <= 1 || scope.Flat)
        {
            return scope.BaseLockout;
        }

        // Cap the shift before it can overflow the multiplication; anything past this is clamped
        // to MaxLockout anyway.
        var shift = Math.Min(bursts - 1, 16);
        var scaled = scope.BaseLockout * Math.Pow(2, shift);
        return scaled > MaxLockout ? MaxLockout : scaled;
    }

    private void Prune(DateTimeOffset now)
    {
        if (entries.Count <= PruneThreshold)
        {
            return;
        }

        foreach (var pair in entries)
        {
            if (now - pair.Value.LastFailure > BurstMemory && pair.Value.LockedUntil <= now)
            {
                entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Entry(
        int Failures,
        DateTimeOffset FirstFailure,
        DateTimeOffset LockedUntil,
        int Bursts,
        DateTimeOffset LastFailure);
}

/// <summary>
/// One counter in <see cref="KioskThrottleRegistry"/>: what is being counted, how many failures it
/// tolerates in how long, and how long the first lockout lasts before the doubling starts.
/// </summary>
/// <param name="Key">Identifies the counter. Never derived from a secret.</param>
/// <param name="MaxAttempts">Failures tolerated inside <paramref name="Window"/>.</param>
/// <param name="Window">How long failures accumulate for.</param>
/// <param name="BaseLockout">The first lockout; each further consecutive burst doubles it.</param>
/// <param name="Flat">
/// True to disable the doubling, so every lockout on this scope lasts <paramref name="BaseLockout"/>.
/// For scopes whose KEY is attacker-controlled, where escalation is a denial of service rather than
/// a guessing bound.
/// </param>
public sealed record KioskThrottleScope(
    string Key,
    int MaxAttempts,
    TimeSpan Window,
    TimeSpan BaseLockout,
    bool Flat = false);
