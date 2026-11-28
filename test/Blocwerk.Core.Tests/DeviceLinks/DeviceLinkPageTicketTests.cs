// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.DeviceLinks;
using Blocwerk.Web.DeviceLinks;
using Microsoft.AspNetCore.DataProtection;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>The consent page's stamp: genuine, for this user and request, and at most five minutes old.</summary>
public class DeviceLinkPageTicketTests
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private static readonly DeviceLinkRequest Request = new("state-0123456789abcdef", Challenge, "Test iPhone");

    private readonly ManualTime clock = new();
    private readonly DeviceLinkPageTicket tickets;
    private readonly Guid user = Guid.NewGuid();

    public DeviceLinkPageTicketTests()
    {
        tickets = new DeviceLinkPageTicket(new EphemeralDataProtectionProvider(), clock);
    }

    [Fact]
    public void FreshTicket_IsValid_UpToFiveMinutes()
    {
        var ticket = tickets.Issue(user, Request);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(tickets.IsValid(ticket, user, Request));
    }

    [Fact]
    public void OlderThanFiveMinutes_IsRefused()
    {
        var ticket = tickets.Issue(user, Request);

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False(tickets.IsValid(ticket, user, Request));
    }

    [Fact]
    public void AnotherUserOrChallenge_IsRefused()
    {
        var ticket = tickets.Issue(user, Request);

        Assert.False(tickets.IsValid(ticket, Guid.NewGuid(), Request));
        Assert.False(tickets.IsValid(ticket, user, Request with { CodeChallenge = Challenge[..^1] + "X" }));
    }

    [Fact]
    public void AnotherStateOrDeviceName_IsRefused()
    {
        var ticket = tickets.Issue(user, Request);

        Assert.False(tickets.IsValid(ticket, user, Request with { State = "state-fedcba9876543210" }));
        Assert.False(tickets.IsValid(ticket, user, Request with { DeviceName = "Other iPhone" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    public void MissingOrForgedTicket_IsRefused(string? ticket)
    {
        Assert.False(tickets.IsValid(ticket, user, Request));
    }
}
