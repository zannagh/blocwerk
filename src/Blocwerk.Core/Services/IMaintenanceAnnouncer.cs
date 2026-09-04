namespace Blocwerk.Core.Services;

/// <summary>
/// Holds the one in-flight "this server is about to be updated" notice, so browsers and kiosk
/// tablets can show a sentence instead of a bare reconnect spinner when the container is recreated
/// underneath them.
/// </summary>
/// <remarks>
/// Deliberately a sibling of <see cref="IDomainChangeNotifier"/> rather than another
/// <c>DomainChange</c> scope: a domain change carries data ABOUT the domain, while this carries a
/// fact about the PROCESS, and mixing the two would make every cache subscriber filter for a kind
/// of event it can never act on.
/// </remarks>
public interface IMaintenanceAnnouncer
{
    /// <summary>
    /// The active announcement, or null when none was ever raised or the last one has expired.
    /// Expiry is decided on every read, so no timer has to fire for the notice to disappear.
    /// </summary>
    MaintenanceAnnouncement? Current { get; }

    /// <summary>
    /// Raised on the thread that announced. Subscribers must be quick and non-throwing; anything
    /// touching a Blazor circuit should marshal via <c>InvokeAsync</c> rather than block here.
    /// </summary>
    event Action<MaintenanceAnnouncement>? Announced;

    /// <summary>
    /// Records an announcement, replacing any earlier one.
    /// </summary>
    /// <param name="message">
    /// Optional text to show. It is trimmed, stripped of markup and control characters and capped
    /// at <see cref="MaintenanceAnnouncer.MaxMessageLength"/>; blank becomes null.
    /// </param>
    /// <param name="ttl">
    /// How long the notice stays live. Clamped into
    /// [<see cref="MaintenanceAnnouncer.MinTtl"/>, <see cref="MaintenanceAnnouncer.MaxTtl"/>]; a
    /// non-positive value falls back to <see cref="MaintenanceAnnouncer.DefaultTtl"/>.
    /// </param>
    /// <returns>The announcement as it was actually recorded, after clamping.</returns>
    MaintenanceAnnouncement Announce(string? message, TimeSpan ttl);
}
