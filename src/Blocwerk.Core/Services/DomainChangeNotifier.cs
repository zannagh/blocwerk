namespace Blocwerk.Core.Services;

/// <summary>
/// Default in-memory <see cref="IDomainChangeNotifier"/>. Publishing is synchronous and defensive:
/// a throwing or slow subscriber must never break the mutation that triggered the notification
/// (publishing runs right after a successful <c>SaveChanges</c>) nor stop the other subscribers.
/// </summary>
public sealed class DomainChangeNotifier : IDomainChangeNotifier
{
    public event Action<DomainChange>? Changed;

    public void Publish(DomainChange change)
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
                ((Action<DomainChange>)handler)(change);
            }
            catch
            {
                // One circuit's cache/handler failing must not fail the save path or starve the
                // other subscribers. There is nothing actionable to do with the exception here.
            }
        }
    }
}
