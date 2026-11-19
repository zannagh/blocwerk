// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>The name Add Variant suggests.</summary>
public class BoulderVariantNamesTests
{
    [Theory]
    [InlineData("Crimp Line", "", "Crimp Line Variant")]
    [InlineData("Crimp Line", "crimp line variant", "Crimp Line Variant 2")]
    [InlineData("Crimp Line Variant", "Crimp Line Variant", "Crimp Line Variant 2")]
    [InlineData("Crimp Line Variant 2", "Crimp Line Variant,Crimp Line Variant 2", "Crimp Line Variant 3")]
    public void CountsOnFromTheBaseName(string origin, string taken, string expected)
    {
        Assert.Equal(expected, BoulderVariantNames.Suggest(origin, taken.Split(',')));
    }

    [Fact]
    public void StaysWithinTheNameLength()
    {
        Assert.True(BoulderVariantNames.Suggest(new string('x', 200), []).Length <= 128);
    }
}
