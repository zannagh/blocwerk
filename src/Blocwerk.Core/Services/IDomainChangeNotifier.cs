namespace Blocwerk.Core.Services;

/// <summary>
/// In-process publish/subscribe for domain changes. Registered as a singleton so a mutation on any
/// circuit (or a background service) can notify every other circuit's cache. Single self-hosted
/// instance, so an in-memory notifier is sufficient — a scaled-out deployment would need a backplane
/// (e.g. Redis) behind this same interface.
/// </summary>
public interface IDomainChangeNotifier
{
    /// <summary>
    /// Raised for every published change, on the thread that published it. Subscribers must be
    /// quick and non-throwing; anything that touches a Blazor circuit should marshal via
    /// <c>InvokeAsync</c> rather than block here.
    /// </summary>
    event Action<DomainChange>? Changed;

    /// <summary>Broadcasts a change to all current subscribers.</summary>
    void Publish(DomainChange change);
}
