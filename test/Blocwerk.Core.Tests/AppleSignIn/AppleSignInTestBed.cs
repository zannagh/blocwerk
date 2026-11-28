// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
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
        Nonces = new AppleNonceStore(contexts);
        Service = new AppleSignInService(
            contexts,
            new StubAppleValidator(),
            Nonces,
            new ApiKeyService(contexts, NullLogger<ApiKeyService>.Instance),
            NullLogger<AppleSignInService>.Instance,
            kiosk);
    }

    public WallTestHarness Harness { get; }

    public InterceptedDbContextFactory Contexts { get; }

    public IKioskContext Kiosk { get; }

    public AppleNonceStore Nonces { get; }

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

    public Task<User> AddUserAsync(string identifier) => MeProfileControllerTests.AddUserAsync(Harness, identifier);

    /// <summary>Links <paramref name="subject"/> to <paramref name="userId"/> with a fresh nonce.</summary>
    public async Task<AppleLinkOutcome> LinkAsync(Guid userId, string subject) =>
        await Service.LinkAsync(userId, StubAppleValidator.Token(subject), await Nonces.IssueAsync());

    /// <summary>Signs <paramref name="subject"/> in from <paramref name="device"/> with a fresh nonce.</summary>
    public async Task<AppleSignInResult> SignInAsync(string subject, string device = "Test iPhone") =>
        await Service.SignInAsync(StubAppleValidator.Token(subject), await Nonces.IssueAsync(), device);

    public void Dispose()
    {
        Harness.Dispose();
    }
}
