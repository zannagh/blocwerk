// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Views;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>A fixed device: a 1920 rung, in Berlin (two hours east of UTC in summer, one in winter).</summary>
internal sealed class FakeViewEnv : IClientViewEnv
{
    public static readonly ClientViewFacts Facts = new(1920, TimeSpan.FromHours(2));

    public Task<ClientViewFacts> FactsAsync() => Task.FromResult(Facts);

    public Task<IReadOnlyList<TimeSpan>> OffsetsAsync(IReadOnlyList<DateTimeOffset> moments) =>
        Task.FromResult<IReadOnlyList<TimeSpan>>(moments.Select(m => TimeSpan.FromHours(m.Month is >= 4 and <= 10 ? 2 : 1)).ToList());
}
