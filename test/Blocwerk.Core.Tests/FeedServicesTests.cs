// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.Core.Tests;

/// <summary>The feed's registrations resolve, and PR 1.2a ships with no mapper, so nothing writes rows yet.</summary>
public sealed class FeedServicesTests
{
    [Fact]
    public void TheFeedResolves_WithTheHubAsItsOwnHostedService_AndNoMapper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RootDbContextFactory>(new TestRootDbContextFactory(TestDbContextFactory.IsolatedDatabase()));
        services.AddWallFeed();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<FeedInterceptor>());
        Assert.True(provider.GetRequiredService<FeedMapperRegistry>().IsEmpty);
        var hub = provider.GetRequiredService<IWallFeedHub>();
        Assert.Contains(provider.GetServices<IHostedService>(), s => ReferenceEquals(s, hub));
    }
}
