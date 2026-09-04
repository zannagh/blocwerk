using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The deploy hook's half of the contract: how <c>etaSeconds</c> on the wire becomes the notice's
/// lifetime, and what happens at the edges of that translation.
/// </summary>
/// <remarks>
/// <see cref="MaintenanceAnnouncerTests"/> covers the clamp as the announcer applies it; this covers
/// the DERIVATION in front of it, which is a separate piece of arithmetic with its own bound
/// (<c>Math.Min(seconds, MaxTtl)</c>) and its own notion of "absent". The two disagree about one
/// thing on purpose — the controller treats a non-positive eta as "not chosen" and never passes it
/// on — and nothing tested that before, so the whole request path could have been deleted and the
/// suite would have stayed green.
/// </remarks>
public class MaintenanceAnnouncementControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    // The ordinary case: the hook's own default, taken at face value.
    [InlineData(600, 600)]
    [InlineData(120, 120)]

    // Below the announcer's floor: raised to MinTtl rather than refused. A notice that flashes and
    // vanishes is worse than no notice, and the hook must never fail to announce over an argument.
    [InlineData(5, 10)]
    [InlineData(1, 10)]

    // Above the ceiling: clamped to MaxTtl. A banner nobody can dismiss is the failure mode here,
    // so the bound is not negotiable — and int.MaxValue must clamp, not overflow.
    [InlineData(60 * 60 * 24, 60 * 30)]
    [InlineData(int.MaxValue, 60 * 30)]

    // "The caller did not choose" — the default window, not an error.
    [InlineData(0, 300)]
    [InlineData(-1, 300)]
    [InlineData(null, 300)]
    public void Announce_DerivesTheNoticesLifetimeFromEtaSeconds(int? etaSeconds, int expectedSeconds)
    {
        var clock = new MutableTestClock(Start);
        var announcer = new MaintenanceAnnouncer(clock);
        var controller = BuildController(announcer);

        var response = Announced(controller.Announce(new MaintenanceAnnounceRequest { EtaSeconds = etaSeconds }));

        Assert.Equal(Start.AddSeconds(expectedSeconds), response.MaintenanceExpiresAt);

        // ...and the announcer really holds it, rather than the controller merely reporting it.
        Assert.Equal(Start.AddSeconds(expectedSeconds), announcer.Current!.ExpiresAt);
    }

    /// <summary>
    /// The bounds the derivation must never be talked past, stated against the announcer's own
    /// constants so a change to either one cannot silently drift away from the other.
    /// </summary>
    [Fact]
    public void Announce_NeverExceedsTheAnnouncersBounds_ForAnyEta()
    {
        int[] etas = [int.MinValue, -1, 0, 1, 9, 10, 11, 300, 1800, 1801, int.MaxValue];

        foreach (var eta in etas)
        {
            var clock = new MutableTestClock(Start);
            var controller = BuildController(new MaintenanceAnnouncer(clock));

            var response = Announced(controller.Announce(new MaintenanceAnnounceRequest { EtaSeconds = eta }));
            var ttl = response.MaintenanceExpiresAt!.Value - Start;

            Assert.InRange(ttl, MaintenanceAnnouncer.MinTtl, MaintenanceAnnouncer.MaxTtl);
        }
    }

    /// <summary>
    /// An empty body is a valid announcement: the hook may have nothing to say beyond "I am about
    /// to restart". A null request must not throw — a 500 here would be swallowed by the hook as a
    /// warning nobody reads, and the notice would silently never fire.
    /// </summary>
    [Fact]
    public void Announce_AcceptsAnAbsentBody()
    {
        var controller = BuildController(new MaintenanceAnnouncer(new MutableTestClock(Start)));

        var response = Announced(controller.Announce(null));

        Assert.True(response.Maintenance);
        Assert.Null(response.Message);
        Assert.Equal(Start + MaintenanceAnnouncer.DefaultTtl, response.MaintenanceExpiresAt);
    }

    /// <summary>
    /// The response reports what was actually RECORDED, not what was asked for, so the hook can log
    /// the truth. The message therefore comes back sanitised.
    /// </summary>
    [Fact]
    public void Announce_EchoesTheRecordedStateAndThisProcessesIdentity()
    {
        var controller = BuildController(new MaintenanceAnnouncer(new MutableTestClock(Start)));

        var response = Announced(controller.Announce(new MaintenanceAnnounceRequest
        {
            Message = "<b>Updating</b>\nback soon",
            EtaSeconds = 600,
        }));

        Assert.Equal("bUpdating/b back soon", response.Message);
        Assert.True(response.Maintenance);

        // The same id /alive reports, which is what lets a client tell "announced" from "replaced".
        Assert.Equal(Blocwerk.Web.State.ProcessInstance.Id, response.InstanceId);
    }

    private static AliveResponse Announced(ActionResult<AliveResponse> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<AliveResponse>(ok.Value);
    }

    private static MaintenanceAnnouncementController BuildController(IMaintenanceAnnouncer announcer)
    {
        return new MaintenanceAnnouncementController(
            announcer,
            NullLogger<MaintenanceAnnouncementController>.Instance)
        {
            // The action logs the calling key's id off the principal, so there has to be one.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
