using System.Text;

namespace Blocwerk.Core.Services;

/// <summary>
/// Default in-memory <see cref="IMaintenanceAnnouncer"/>. One announcement at a time, held in a
/// field, expired on read.
/// </summary>
/// <remarks>
/// <b>Single instance, in memory, on purpose.</b> The announcement describes THIS process — it is
/// raised seconds before the container is recreated and is meaningless to anyone else — so there
/// is nothing to share and nothing to persist. The same assumption the domain-change notifier and
/// <see cref="KioskAnonymousSettingThrottle"/> already make: a scaled-out deployment would need a
/// backplane behind this interface, and a restart losing the notice is exactly what a restart
/// means here.
/// <para>
/// <b>Expiry is evaluated on read, never by a timer.</b> A timer would have to be disposed, would
/// fire on a thread pool thread into subscribers that may be gone, and would still leave a window
/// where <c>Current</c> disagreed with the clock. Comparing against <see cref="TimeProvider"/> when
/// somebody asks is both cheaper and impossible to get out of sync — and it is what makes the
/// notice self-healing: if the deploy fails, or never happens at all, the banner clears itself
/// instead of sticking around until the next restart.
/// </para>
/// </remarks>
public sealed class MaintenanceAnnouncer : IMaintenanceAnnouncer
{
    /// <summary>Used when the caller does not say how long the update is expected to take.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>A notice shorter than this is not worth showing; it would flash and vanish.</summary>
    public static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Hard ceiling. A caller asking for hours is either confused or hostile, and the cost of
    /// being wrong is a banner nobody can dismiss, so the ceiling is not negotiable.
    /// </summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);

    /// <summary>The message is a one-line notice, not a change log.</summary>
    public const int MaxMessageLength = 200;

    private readonly TimeProvider timeProvider;
    private readonly object gate = new();

    private MaintenanceAnnouncement? current;

    public MaintenanceAnnouncer()
        : this(TimeProvider.System)
    {
    }

    public MaintenanceAnnouncer(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    public event Action<MaintenanceAnnouncement>? Announced;

    public MaintenanceAnnouncement? Current
    {
        get
        {
            var now = timeProvider.GetUtcNow();
            lock (gate)
            {
                if (current is null)
                {
                    return null;
                }

                if (current.ExpiresAt <= now)
                {
                    // Dropped rather than merely hidden, so the reference does not outlive its
                    // usefulness and every later read is a null check instead of a comparison.
                    current = null;
                    return null;
                }

                return current;
            }
        }
    }

    public MaintenanceAnnouncement Announce(string? message, TimeSpan ttl)
    {
        var now = timeProvider.GetUtcNow();
        var announcement = new MaintenanceAnnouncement(Sanitize(message), now, now + ClampTtl(ttl));

        lock (gate)
        {
            current = announcement;
        }

        Publish(announcement);
        return announcement;
    }

    /// <summary>
    /// Non-positive means "the caller did not choose", which is the default rather than an error:
    /// the deploy hook must never fail to announce because of an argument.
    /// </summary>
    private static TimeSpan ClampTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return DefaultTtl;
        }

        if (ttl < MinTtl)
        {
            return MinTtl;
        }

        return ttl > MaxTtl ? MaxTtl : ttl;
    }

    /// <summary>
    /// The message is rendered to end users, so it is reduced to plain single-line text here — at
    /// the point where it enters the process — rather than trusting every future renderer to
    /// encode it. Markup characters and control characters are dropped outright instead of being
    /// escaped, because there is no legitimate notice that needs them.
    /// </summary>
    private static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(message.Length, MaxMessageLength));
        foreach (var c in message)
        {
            if (builder.Length == MaxMessageLength)
            {
                break;
            }

            if (c is '<' or '>' or '&' or '"')
            {
                continue;
            }

            // Newlines and tabs included: this is one line in a banner.
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private void Publish(MaintenanceAnnouncement announcement)
    {
        var handlers = Announced;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<MaintenanceAnnouncement>)handler)(announcement);
            }
            catch
            {
                // A circuit that has already gone away must not stop the notice reaching the ones
                // still listening — and there is no caller who could act on the exception anyway.
            }
        }
    }
}
