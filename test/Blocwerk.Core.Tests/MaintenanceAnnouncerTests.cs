using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The "server is updating" notice. What matters is that it always dies on its own — a deploy that
/// fails, or never happens, must not leave a banner nobody can clear — and that a caller cannot
/// talk it into an absurd lifetime or into carrying markup.
/// </summary>
public class MaintenanceAnnouncerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Current_IsNullBeforeAnythingIsAnnounced()
    {
        var announcer = new MaintenanceAnnouncer(new MutableTestClock(Start));

        Assert.Null(announcer.Current);
    }

    [Fact]
    public void Announce_MakesCurrentLive_AndItExpiresOnItsOwn()
    {
        var clock = new MutableTestClock(Start);
        var announcer = new MaintenanceAnnouncer(clock);

        var announced = announcer.Announce("Updating", TimeSpan.FromMinutes(2));

        Assert.NotNull(announcer.Current);
        Assert.Equal("Updating", announcer.Current!.Message);
        Assert.Equal(Start, announced.AnnouncedAt);
        Assert.Equal(Start.AddMinutes(2), announced.ExpiresAt);

        // One tick before the deadline it is still shown...
        clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Assert.NotNull(announcer.Current);

        // ...and at it, it is gone, with nothing having had to fire.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(announcer.Current);
    }

    [Fact]
    public void Announce_ClampsTheTtlAtBothEnds_AndDefaultsWhenNoneIsGiven()
    {
        var clock = new MutableTestClock(Start);
        var announcer = new MaintenanceAnnouncer(clock);

        var absurd = announcer.Announce(null, TimeSpan.FromDays(7));
        Assert.Equal(Start + MaintenanceAnnouncer.MaxTtl, absurd.ExpiresAt);

        var tiny = announcer.Announce(null, TimeSpan.FromMilliseconds(1));
        Assert.Equal(Start + MaintenanceAnnouncer.MinTtl, tiny.ExpiresAt);

        var unset = announcer.Announce(null, TimeSpan.Zero);
        Assert.Equal(Start + MaintenanceAnnouncer.DefaultTtl, unset.ExpiresAt);

        var negative = announcer.Announce(null, TimeSpan.FromMinutes(-5));
        Assert.Equal(Start + MaintenanceAnnouncer.DefaultTtl, negative.ExpiresAt);
    }

    [Fact]
    public void Announce_SanitizesAndCapsTheMessage()
    {
        var announcer = new MaintenanceAnnouncer(new MutableTestClock(Start));

        var withMarkup = announcer.Announce("<script>alert(1)</script> back soon", TimeSpan.Zero);
        Assert.Equal("scriptalert(1)/script back soon", withMarkup.Message);

        var multiline = announcer.Announce("line one\nline two", TimeSpan.Zero);
        Assert.Equal("line one line two", multiline.Message);

        var tooLong = announcer.Announce(new string('x', 5000), TimeSpan.Zero);
        Assert.Equal(MaintenanceAnnouncer.MaxMessageLength, tooLong.Message!.Length);

        Assert.Null(announcer.Announce("   ", TimeSpan.Zero).Message);
        Assert.Null(announcer.Announce(null, TimeSpan.Zero).Message);
    }

    [Fact]
    public void Announce_ReplacesTheEarlierNotice_AndNotifiesSubscribers()
    {
        var announcer = new MaintenanceAnnouncer(new MutableTestClock(Start));
        var seen = new List<string?>();

        // A throwing subscriber must not starve the next one, exactly as with the domain notifier.
        announcer.Announced += _ => throw new InvalidOperationException("subscriber is gone");
        announcer.Announced += a => seen.Add(a.Message);

        announcer.Announce("first", TimeSpan.Zero);
        announcer.Announce("second", TimeSpan.Zero);

        Assert.Equal(new[] { "first", "second" }, seen);
        Assert.Equal("second", announcer.Current!.Message);
    }
}
