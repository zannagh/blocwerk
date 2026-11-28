// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Apple;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>The parked code tickets, and the name Apple posts on the first authorization only.</summary>
public class AppleCallbackPartsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATicket_IsTakenOnce_AndNotAfterItExpires()
    {
        var clock = new MutableTestClock(Start);
        var store = new AppleCallbackStore(clock);

        Assert.True(store.Add("code-1", "state-1", "nonce-1", "Ada Lovelace"));
        Assert.False(store.Add("code-1", "state-x", "nonce-x", null));
        Assert.True(store.TryTake("code-1", out var ticket));
        Assert.Equal(("state-1", "nonce-1", "Ada Lovelace"), (ticket.State, ticket.RawNonce, ticket.Name));
        Assert.False(store.TryTake("code-1", out _));

        Assert.True(store.Add("code-2", "state-2", "nonce-2", null));
        clock.Advance(AppleCallbackStore.Ttl + TimeSpan.FromSeconds(1));
        Assert.False(store.TryTake("code-2", out _));
        Assert.False(store.TryTake("never-parked", out _));
        Assert.False(store.Add(new string('c', 513), "state", "nonce", null));
    }

    [Theory]
    [InlineData("""{"name":{"firstName":"Ada","lastName":"Lovelace"},"email":"x@privaterelay.appleid.com"}""", "Ada Lovelace")]
    [InlineData("""{"name":{"firstName":"  Ada "}}""", "Ada")]
    [InlineData("""{"name":{"lastName":"Lovelace"}}""", "Lovelace")]
    [InlineData("""{"email":"x@privaterelay.appleid.com"}""", null)]
    [InlineData("""{"name":{"firstName":"","lastName":""}}""", null)]
    [InlineData("""{"name":"Ada"}""", null)]
    [InlineData("""{"name":{"firstName":"A‮da"}}""", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheName_IsFirstAndLast_AndNothingElse(string? userField, string? expected)
    {
        Assert.Equal(expected, AppleUserName.Parse(userField));
    }
}
