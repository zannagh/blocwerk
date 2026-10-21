// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.Core.Tests;

/// <summary>The feed's registrations resolve, with the production mappers (<see cref="FeedMappers"/>) in the registry.</summary>
public sealed class FeedServicesTests
{
    private static readonly IComparer<Type> TypeNames = Comparer<Type>.Create((a, b) => string.CompareOrdinal(a.FullName, b.FullName));

    [Fact]
    public void TheFeedResolves_WithTheHubAsItsOwnHostedService_AndTheProductionMappers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RootDbContextFactory>(new TestRootDbContextFactory(TestDbContextFactory.IsolatedDatabase()));
        services.AddWallFeed();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<FeedInterceptor>());
        var mapped = provider.GetRequiredService<FeedMapperRegistry>().MappedTypes;
        Assert.Equal(new FeedMapperRegistry(FeedMappers.Create()).MappedTypes.Order(TypeNames), mapped.Order(TypeNames));
        Assert.NotEmpty(mapped);
        var hub = provider.GetRequiredService<IWallFeedHub>();
        Assert.Contains(provider.GetServices<IHostedService>(), s => ReferenceEquals(s, hub));
    }
}
