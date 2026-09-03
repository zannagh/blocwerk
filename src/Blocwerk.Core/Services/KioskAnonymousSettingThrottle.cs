using System.Collections.Concurrent;

namespace Blocwerk.Core.Services;

/// <summary>
/// Caps how many boulders UNATTENDED kiosk tablets may create. In-memory, per process, counted in
/// fixed windows at three scopes: one tablet on one wall, one wall in total, and — as a last-resort
/// circuit breaker only — the whole installation.
/// </summary>
/// <remarks>
/// This is a volume limit, not a guessing limit, which is why it is not
/// <c>KioskThrottleRegistry</c>: that one counts FAILURES and locks out after five, and every call
/// here is a legitimate, successful write. What it bounds is a tablet in a public gym being turned
/// into a boulder-spam faucet by somebody who simply keeps tapping "Publish".
/// <para>
/// <b>Why the budget is per tenant.</b> The first cut had a single global counter of 200/hour on top
/// of the per-tablet 30. Seven saturated tablets — one busy setting day in ONE gym — therefore
/// denied anonymous setting to every other wall in the installation for the rest of the window.
/// That is a cross-tenant denial cliff dressed up as a spam limit: one gym's traffic must never be
/// able to switch the feature off for another gym. So the routine budget is scoped to the wall (the
/// tenant), and to the tablet within it. The installation-wide number is kept only as a genuine
/// backstop and is set far above any plausible real load, so tripping it means something is wrong
/// rather than that somebody had a productive afternoon — and it is logged distinctly for exactly
/// that reason.
/// </para>
/// <para>
/// <b>What it does not do.</b> It is per process, so a multi-instance deployment multiplies the caps
/// by the instance count; the app is single-instance today (the same assumption the cache notifier
/// already makes). It also resets on restart. Both are acceptable because the damage this bounds is
/// junk rows on one wall, deletable by any wall admin — not a credential or an escalation.
/// </para>
/// <para>
/// <b>Checking and recording are separate</b> (see <see cref="Check"/> / <see cref="Record"/>) so a
/// create that the server then REFUSES — an unconsenting setter, a missing wall, a failed save —
/// does not spend the tablet's budget. Only writes that actually landed are counted. The cheap
/// pre-check still runs at the gate, so a caller that is already at its cap is refused before any
/// work is done and cannot spin unlimited failing requests to keep the server busy.
/// </para>
/// </remarks>
public sealed class KioskAnonymousSettingThrottle
{
    /// <summary>Boulders one tablet may create anonymously on one wall inside <see cref="Window"/>.</summary>
    public const int MaxPerKey = 30;

    /// <summary>
    /// Boulders EVERY tablet on ONE wall may create anonymously per window. Four saturated tablets'
    /// worth: a setting crew works one wall from a couple of tablets, so this bounds a compromised
    /// fleet without ever reaching a real session.
    /// </summary>
    public const int MaxPerWall = 120;

    /// <summary>
    /// The installation-wide backstop: 40 fully saturated walls per hour. Deliberately far above any
    /// real load — it exists so a runaway script cannot fill the database while nobody is watching,
    /// NOT to ration ordinary use, and one gym can never spend another's share of it.
    /// </summary>
    public const int MaxGlobal = 5_000;

    /// <summary>The fixed counting window. A setting session is well inside one key's allowance.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Above this many tracked scopes, expired ones are swept before the next admission.</summary>
    private const int PruneThreshold = 2_000;

    private const string GlobalScopeKey = "kioskset:global";

    private static readonly object CountersLock = new();

    private readonly ConcurrentDictionary<string, Counter> counters = new();

    /// <summary>
    /// Reports whether one more anonymous create would be inside every budget, WITHOUT counting it.
    /// Call this at the gate; call <see cref="Record"/> once the write has actually landed.
    /// </summary>
    public KioskAnonymousSettingBudget Check(Guid apiKeyId, Guid wallId, DateTimeOffset now)
    {
        if (counters.Count > PruneThreshold)
        {
            Prune(now);
        }

        if (IsAtCap(KeyScope(apiKeyId, wallId), MaxPerKey, now))
        {
            return KioskAnonymousSettingBudget.TabletCapReached;
        }

        if (IsAtCap(WallScope(wallId), MaxPerWall, now))
        {
            return KioskAnonymousSettingBudget.WallCapReached;
        }

        if (IsAtCap(GlobalScopeKey, MaxGlobal, now))
        {
            return KioskAnonymousSettingBudget.InstallationCapReached;
        }

        return KioskAnonymousSettingBudget.Allowed;
    }

    /// <summary>
    /// Counts one anonymous create that actually happened, at all three scopes. Never refuses —
    /// the refusal is <see cref="Check"/>'s job, and a write that has already been committed must be
    /// counted whatever the counters now say.
    /// </summary>
    public void Record(Guid apiKeyId, Guid wallId, DateTimeOffset now)
    {
        RecordScope(KeyScope(apiKeyId, wallId), now);
        RecordScope(WallScope(wallId), now);
        RecordScope(GlobalScopeKey, now);
    }

    /// <summary>
    /// <see cref="Check"/> followed by <see cref="Record"/>, for callers with nothing to fail
    /// between the two. Nothing is counted when the answer is false, so a refused attempt cannot
    /// push the window out.
    /// </summary>
    public bool TryRecord(Guid apiKeyId, Guid wallId, DateTimeOffset now)
    {
        if (Check(apiKeyId, wallId, now) != KioskAnonymousSettingBudget.Allowed)
        {
            return false;
        }

        Record(apiKeyId, wallId, now);
        return true;
    }

    /// <summary>Forgets everything. For tests, and for a wall admin's "it's stuck" escape hatch.</summary>
    public void Reset()
    {
        counters.Clear();
    }

    private static string KeyScope(Guid apiKeyId, Guid wallId)
    {
        return $"kioskset:key:{apiKeyId:N}:{wallId:N}";
    }

    private static string WallScope(Guid wallId)
    {
        return $"kioskset:wall:{wallId:N}";
    }

    private bool IsAtCap(string scope, int cap, DateTimeOffset now)
    {
        if (!counters.TryGetValue(scope, out var counter))
        {
            return false;
        }

        lock (CountersLock)
        {
            return counter.WindowStart + Window > now && counter.Count >= cap;
        }
    }

    private void RecordScope(string scope, DateTimeOffset now)
    {
        lock (CountersLock)
        {
            var counter = counters.GetOrAdd(scope, _ => new Counter { WindowStart = now });
            if (counter.WindowStart + Window <= now)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            counter.Count++;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (scope, counter) in counters)
        {
            if (counter.WindowStart + Window <= now)
            {
                counters.TryRemove(scope, out _);
            }
        }
    }

    private sealed class Counter
    {
        public DateTimeOffset WindowStart { get; set; }

        public int Count { get; set; }
    }
}
