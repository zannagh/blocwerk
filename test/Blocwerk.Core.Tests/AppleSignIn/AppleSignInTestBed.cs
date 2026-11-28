// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Blocwerk.Web.DeviceLinks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>
/// The real Apple sign-in service over an isolated SQLite database with the production feed interceptor, the real key
/// service and nonce store, and <see cref="StubAppleValidator"/> for the token check.
/// </summary>
internal sealed class AppleSignInTestBed : IDisposable
{
    private AppleSignInTestBed(WallTestHarness harness, InterceptedDbContextFactory contexts, IKioskContext kiosk)
    {
        Harness = harness;
        Contexts = contexts;
        Kiosk = kiosk;
        Codec = new DataProtectionAppleNonceCodec(new EphemeralDataProtectionProvider());
        Nonces = new AppleNonceStore(contexts, Codec);
        ApiKeys = new ApiKeyService(contexts, NullLogger<ApiKeyService>.Instance);
        Service = new AppleSignInService(
            contexts,
            new StubAppleValidator(),
            Nonces,
            ApiKeys,
            NullLogger<AppleSignInService>.Instance,
            kiosk);
    }

    public WallTestHarness Harness { get; }

    public InterceptedDbContextFactory Contexts { get; }

    public IKioskContext Kiosk { get; }

    public DataProtectionAppleNonceCodec Codec { get; }

    public AppleNonceStore Nonces { get; }

    public ApiKeyService ApiKeys { get; }

    public AppleSignInService Service { get; }

    public static AppleSignInTestBed Create()
    {
        var harness = new WallTestHarness();
        var feed = new FeedInterceptor(
            new FeedMapperRegistry(FeedMappers.Create()), new RecordingFeedSignal(), NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());
        return new AppleSignInTestBed(
            harness,
            new InterceptedDbContextFactory(harness.DbContextFactory.ConnectionString, [feed]),
            Substitute.For<IKioskContext>());
    }

    /// <summary>An account as every account before web Sign in with Apple is: with a provider identity (GitHub).</summary>
    public async Task<User> AddUserAsync(string identifier)
    {
        var user = await MeProfileControllerTests.AddUserAsync(Harness, identifier);
        await using var db = Harness.CreateContext();
        db.UserIdentities.Add(new UserIdentity { UserId = user.Id, Provider = "github", ProviderUserId = $"gh-{user.Id:N}" });
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>An account with no password and no provider identity, as web Sign in with Apple makes one.</summary>
    public Task<User> AddAppleOnlyUserAsync(string identifier) => MeProfileControllerTests.AddUserAsync(Harness, identifier);

    /// <summary>A fresh device key of <paramref name="userId"/>, as the app holds right after signing in.</summary>
    public async Task<Guid> DeviceKeyAsync(Guid userId, string device = "Linking iPhone") =>
        (await ApiKeys.CreateDeviceKeyAsync(userId, "iPhone: " + device)).Key.Id;

    /// <summary>Links <paramref name="subject"/> to <paramref name="userId"/> from a fresh device key with a fresh nonce.</summary>
    public async Task<AppleLinkOutcome> LinkAsync(Guid userId, string subject) =>
        await Service.LinkAsync(userId, await DeviceKeyAsync(userId), StubAppleValidator.Token(subject), Nonces.Issue());

    /// <summary>Signs <paramref name="subject"/> in from <paramref name="device"/> with a fresh nonce.</summary>
    public Task<AppleSignInResult> SignInAsync(string subject, string device = "Test iPhone") =>
        Service.SignInAsync(StubAppleValidator.Token(subject), Nonces.Issue(), device);

    public void Dispose()
    {
        Harness.Dispose();
    }
}
