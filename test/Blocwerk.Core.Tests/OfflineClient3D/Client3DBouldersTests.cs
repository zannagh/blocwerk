// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.ThreeD;
using Blocwerk.Contracts.Documents;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClient3D;

/// <summary>
/// A boulder's part in the offline 3D view, from the cached documents, as the Server's <c>Wall3DViewBuilder.RoleOf</c>:
/// start and top by type, foot-only as a foothold, the rest hand holds, and the other holds of its foot colour as colour
/// footholds.
/// </summary>
public sealed class Client3DBouldersTests
{
    private static readonly Guid RedSpare = Guid.Parse("d0000000-0000-0000-0000-000000000009");
    private static readonly Guid BlueSpare = Guid.Parse("d0000000-0000-0000-0000-00000000000a");

    [Fact]
    public void TheRoles_FollowTheServers()
    {
        var boulder = Client3DBoulders.Of(CrimpLine(), HoldsDoc())!;

        Assert.Equal(Crimp.ToString(), boulder.Id);
        Assert.Equal("Crimp Line", boulder.Name);
        Assert.Equal("Start", boulder.Roles[HoldStart.ToString()]);
        Assert.Equal("Top", boulder.Roles[HoldTop.ToString()]);
        Assert.Equal("Foot", boulder.Roles[HoldRight.ToString()]);
        Assert.Equal("Hand", Client3DBoulders.Of(DraftBoulder(), HoldsDoc())!.Roles[HoldTop.ToString()]);
    }

    [Fact]
    public void TheFootColourRule_AddsTheOtherHoldsOfThatColour_Only()
    {
        var spare = HoldsDoc().Holds[0];
        var holds = new HoldsDocument(1, Wall, 2, [.. HoldsDoc().Holds, spare with { Id = RedSpare }, spare with { Id = BlueSpare, Color = "blue" }]);

        Assert.Equal([RedSpare.ToString()], Client3DBoulders.Of(CrimpLine(), holds)!.ColorFoot);
        Assert.Empty(Client3DBoulders.Of(CrimpLine(), null)!.ColorFoot);
        Assert.Empty(Client3DBoulders.Of(DraftBoulder(), holds)!.ColorFoot);
    }

    [Fact]
    public void AWallsView_HighlightsNoBoulder()
    {
        Assert.Null(Client3DBoulders.Of(null, HoldsDoc()));
    }
}
