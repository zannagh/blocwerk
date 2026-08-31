namespace Blocwerk.Web.State;

/// <summary>
/// A handle to one tracked editing session. Disposing it removes the session's entry from
/// <see cref="EditActivityRegistry"/>. Dispose is idempotent — disposing more than once is safe.
/// </summary>
public interface IEditLease : IDisposable
{
}
