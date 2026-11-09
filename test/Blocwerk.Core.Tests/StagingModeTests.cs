// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Blocwerk.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>Blocwerk:Staging</c>: off by default; on, every response says noindex and neither e-mail nor web push goes out
/// (each logs instead), whatever SMTP and VAPID are set to.
/// </summary>
public class StagingModeTests
{
    [Fact]
    public void Staging_IsOffByDefault_AndBindsFromConfiguration()
    {
        Assert.False(new BlocwerkSettings(Config(new())).Staging);
        Assert.True(new BlocwerkSettings(Config(new() { ["Blocwerk:Staging"] = "true" })).Staging);
        Assert.False(new BlocwerkSettings(Config(new() { ["Blocwerk:Staging"] = "nonsense" })).Staging);
    }

    [Theory]
    [InlineData(true, "noindex, nofollow")]
    [InlineData(false, null)]
    public async Task RobotsHeader_IsOnlyOnStaging(bool staging, string? expected)
    {
        var context = new DefaultHttpContext();
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseStagingHeaders(staging);

        await app.Build()(context);

        Assert.Equal(expected, context.Response.Headers["X-Robots-Tag"].ToString() is { Length: > 0 } tag ? tag : null);
    }

    [Fact]
    public async Task Email_OnStaging_IsLoggedNotSent_EvenWithoutSmtp()
    {
        var logger = Substitute.For<ILogger<EmailSender>>();
        var sender = new EmailSender(Settings(staging: true), logger);

        await sender.SendAsync("someone@example.com", "subject", "<p>body</p>");

        Assert.True(LoggedInformation(logger));
    }

    [Fact]
    public async Task Email_OffStaging_StillRefusesWithoutSmtp()
    {
        var sender = new EmailSender(Settings(staging: false), Substitute.For<ILogger<EmailSender>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync("someone@example.com", "s", "b"));
    }

    [Fact]
    public async Task Push_OnStaging_IsLoggedNotQueued_AndHandsOutNoPublicKey()
    {
        var logger = Substitute.For<ILogger<PushNotificationService>>();
        var vapid = new VapidOptions { Subject = "mailto:a@b.c", PublicKey = "pub", PrivateKey = "priv", Suppressed = true };
        var push = new PushNotificationService(
            new RootDbContextFactory(new DbContextOptions<BlocwerkDbContext>()), new PushSendQueue(), vapid, logger);

        await push.NotifyCommentAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.True(LoggedInformation(logger));
        Assert.Null(push.PublicKey);
    }

    [Fact]
    public void Push_OffStaging_KeepsItsPublicKey()
    {
        var vapid = new VapidOptions { Subject = "mailto:a@b.c", PublicKey = "pub", PrivateKey = "priv" };
        var push = new PushNotificationService(
            new RootDbContextFactory(new DbContextOptions<BlocwerkDbContext>()), new PushSendQueue(), vapid,
            Substitute.For<ILogger<PushNotificationService>>());

        Assert.Equal("pub", push.PublicKey);
    }

    private static bool LoggedInformation(object logger) =>
        logger.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Information);

    private static BlocwerkSettings Settings(bool staging) =>
        new(Config(new() { ["Blocwerk:Staging"] = staging ? "true" : "false" }));

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
