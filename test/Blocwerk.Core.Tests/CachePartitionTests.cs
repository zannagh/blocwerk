// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The partition rules (design §6.6, S5a): the label follows the cookie identity, a kiosk tablet has one label for every
/// identity it takes, a tablet that may not cache its wall is <c>none</c>, and a share token changes nothing.
/// </summary>
public class CachePartitionTests
{
    private static readonly Guid KeyId = Guid.Parse("6f1c0a6e-0000-4000-8000-00000000000a");
    private static readonly Guid WallId = Guid.Parse("0b8f2c1d-0000-4000-8000-0000000000b1");

    private readonly ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
    private readonly IKioskContext kiosk = Substitute.For<IKioskContext>();
    private readonly IKioskKeyValidator kioskKeys = KeysThatAreLive();
    private User? caller;

    public CachePartitionTests()
    {
        currentUser.GetCurrentUserAsync().Returns(_ => caller is { } user
            ? Task.FromResult(user)
            : Task.FromException<User>(new UnauthorizedAccessException()));
    }

    [Fact]
    public async Task NobodySignedIn_IsAnon()
    {
        Assert.Equal(CachePartitions.Anonymous, await Resolver().ResolveAsync());
        Assert.Equal(CachePartitions.Anonymous, await new CachePartitionResolver(new FixedPartitionKey(1), currentUser).ResolveAsync());
    }

    [Fact]
    public async Task ASignedInUser_GetsAStableLabelOfTheirOwn_UnderTheKey()
    {
        caller = new User { Identifier = "a@test" };
        var label = await Resolver().ResolveAsync();

        Assert.Matches("^u-[a-z2-7]{20}$", label);
        Assert.Equal(label, await Resolver().ResolveAsync());
        Assert.DoesNotContain(caller.Id.ToString("N")[..8], label);
        Assert.NotEqual(label, await Resolver(new FixedPartitionKey(2)).ResolveAsync());
        caller = new User { Identifier = "b@test" };
        Assert.NotEqual(label, await Resolver().ResolveAsync());
    }

    [Fact]
    public async Task AKioskTablet_HasOneLabel_ForEveryIdentityItTakes()
    {
        Bind(WallId);
        var anonymous = await Resolver().ResolveAsync();
        caller = new User { Identifier = "member@test" };
        var actingAs = await Resolver().ResolveAsync();

        Assert.Matches("^k-[a-z2-7]{20}$", anonymous);
        Assert.Equal(anonymous, actingAs);
        await kiosk.Received().InitializeAsync();

        Bind(Guid.Parse("0b8f2c1d-0000-4000-8000-0000000000b2"));
        Assert.NotEqual(anonymous, await Resolver().ResolveAsync());
    }

    [Fact]
    public async Task AKioskThatMayNotCacheItsWall_IsNone_WhoeverItActsAs()
    {
        Bind(Guid.Empty);
        Assert.Equal(CachePartitions.None, await Resolver().ResolveAsync());
        caller = new User { Identifier = "member@test" };
        Assert.Equal(CachePartitions.None, await Resolver().ResolveAsync());

        Bind(WallId);
        kiosk.KioskApiKeyId.Returns((Guid?)null);
        Assert.Equal(CachePartitions.None, await Resolver().ResolveAsync());
    }

    [Fact]
    public async Task ThePartitionRoute_AnswersTheLabel_AndIsNeverStored()
    {
        caller = new User { Identifier = "a@test" };
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();

        var result = await ManifestEndpoints.HandlePartitionAsync(http, Resolver());
        await result.ExecuteAsync(http);

        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        using var body = JsonDocument.Parse(((MemoryStream)http.Response.Body).ToArray());
        Assert.Equal(await Resolver().ResolveAsync(), body.RootElement.GetProperty("partition").GetString());
    }

    /// <summary>
    /// The route says why nobody is signed in, for an <c>anon</c> caller only (PR 3.5): the sticky marker's reason, else
    /// <c>expired</c>; a signed-in caller gets null.
    /// </summary>
    [Theory]
    [InlineData("anon", null, "expired")]
    [InlineData("anon", "gone", "gone")]
    [InlineData("u-abcdefghijklmnopqrst", "forced", null)]
    public async Task ThePartitionRoute_CarriesTheSignOutReason_ForAnAnonymousCaller(string partition, string? marker, string? expected)
    {
        var resolver = Substitute.For<ICachePartitionResolver>();
        resolver.ResolveAsync().Returns(partition);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        if (marker is not null)
        {
            http.Request.Headers.Cookie = $"{Blocwerk.Authentication.Services.SignOutReason.MarkerCookie}={marker}";
        }

        await (await ManifestEndpoints.HandlePartitionAsync(http, resolver)).ExecuteAsync(http);

        using var body = JsonDocument.Parse(((MemoryStream)http.Response.Body).ToArray());
        var signOut = body.RootElement.GetProperty("signOut");
        Assert.Equal(expected, signOut.ValueKind == JsonValueKind.Null ? null : signOut.GetString());
    }

    /// <summary>A resolver fault answers <c>unknown</c> with a 200: a 5xx would send the worker back to its last label.</summary>
    [Fact]
    public async Task ThePartitionRoute_AnswersUnknown_WhenTheResolverFails()
    {
        var resolver = Substitute.For<ICachePartitionResolver>();
        resolver.ResolveAsync().Returns<string>(_ => throw new InvalidOperationException("boom"));
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();

        await (await ManifestEndpoints.HandlePartitionAsync(http, resolver)).ExecuteAsync(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        using var body = JsonDocument.Parse(((MemoryStream)http.Response.Body).ToArray());
        Assert.Equal("unknown", body.RootElement.GetProperty("partition").GetString());
    }

    private CachePartitionResolver Resolver(ICachePartitionKey? key = null) => new(key ?? new FixedPartitionKey(1), currentUser, kiosk, kioskKeys);

    private static IKioskKeyValidator KeysThatAreLive()
    {
        var keys = Substitute.For<IKioskKeyValidator>();
        keys.IsKeyValidAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return keys;
    }

    private void Bind(Guid wallId)
    {
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(wallId);
        kiosk.KioskApiKeyId.Returns(KeyId);
    }
}
