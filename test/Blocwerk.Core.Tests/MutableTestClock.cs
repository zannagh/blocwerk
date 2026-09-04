namespace Blocwerk.Core.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose "now" only moves when a test moves it, so a TTL can be
/// stepped over without a <c>Thread.Sleep</c> making the suite slow and flaky.
/// </summary>
public sealed class MutableTestClock : TimeProvider
{
    private DateTimeOffset now;

    public MutableTestClock(DateTimeOffset start)
    {
        now = start;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return now;
    }

    public void Advance(TimeSpan by)
    {
        now += by;
    }
}
