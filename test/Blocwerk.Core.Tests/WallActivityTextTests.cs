// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Web.Components.Pages.Walls;

namespace Blocwerk.Core.Tests;

/// <summary>The wall activity card's words for a removal and a leaving.</summary>
public sealed class WallActivityTextTests
{
    [Fact]
    public void ARemoval_NamesThePersonRemoved_AndALeavingReadsAsSuch()
    {
        Assert.Equal("removed Max", WallActivityText.Verb(ActivityType.MemberRemoved, "Max"));
        Assert.Equal("removed a member", WallActivityText.Verb(ActivityType.MemberRemoved, null));
        Assert.Equal("left the wall", WallActivityText.Verb(ActivityType.MemberLeft, "Max"));
        Assert.Equal(("Member", "member"), (WallActivityText.Chip(ActivityType.MemberLeft), WallActivityText.ChipTone(ActivityType.MemberRemoved)));
    }
}
