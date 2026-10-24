// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Views;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>A fixed device: a 1920 rung, two hours east of UTC.</summary>
internal sealed class FakeViewEnv : IClientViewEnv
{
    public static readonly ClientViewFacts Facts = new(1920, TimeSpan.FromHours(2));

    public Task<ClientViewFacts> FactsAsync() => Task.FromResult(Facts);
}
