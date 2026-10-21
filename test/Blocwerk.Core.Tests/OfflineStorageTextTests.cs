// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Settings → "Offline storage" (PR 2.9): the words and numbers the card shows, and the shape
/// <c>bwAssetReport.report()</c> hands the circuit.
/// </summary>
public class OfflineStorageTextTests
{
    private static readonly OfflineStorageFacts Desktop = new(
        Caching: true,
        Kiosk: false,
        Persisted: false,
        Keep3d: true,
        Mobile: false,
        Background3d: true,
        IosTab: false,
        InstallHint: null,
        Paused: false,
        Total: 0,
        Walls: []);

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(5000, "5 KB")]
    [InlineData(150 * 1024, "150 KB")]
    [InlineData(12_998_000, "12.4 MB")]
    [InlineData(1_288_490_189, "1.2 GB")]
    [InlineData(-5, "0 B")]
    public void BytesReadForPeople(long bytes, string expected)
    {
        Assert.Equal(expected, OfflineStorageText.Bytes(bytes));
    }

    [Fact]
    public void BreakdownAlwaysNamesThePhotosAndOnlyTheOtherPartsThatHoldSomething()
    {
        var photosOnly = new OfflineWallUsage("w", 2048, 0, 0, 0, 2048, false, true, 0);
        var all = new OfflineWallUsage("w", 2048, 1024, 3 * 1024 * 1024, 512, 0, false, false, 0);

        Assert.Equal("Photos 2 KB", OfflineStorageText.Breakdown(photosOnly));
        Assert.Equal("Photos 2 KB · Gallery 1 KB · 3D 3.0 MB · Other 512 B", OfflineStorageText.Breakdown(all));
    }

    [Fact]
    public void TheThreeDNoteSaysMobileCachesOnOpenAndCanUseMobileData()
    {
        var mobile = OfflineStorageText.ThreeDNote(Desktop with { Mobile = true, Background3d = false });

        Assert.Contains("only when you open", mobile, StringComparison.Ordinal);
        Assert.Contains("mobile data", mobile, StringComparison.Ordinal);
        Assert.Contains("in the background", OfflineStorageText.ThreeDNote(Desktop), StringComparison.Ordinal);
        Assert.Contains("isn't kept", OfflineStorageText.ThreeDNote(Desktop with { Keep3d = false, Mobile = true }), StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceNamesSafariInAnIosTab()
    {
        Assert.Contains("won't clear", OfflineStorageText.Persistence(Desktop with { Persisted = true }), StringComparison.Ordinal);
        Assert.Contains("Safari", OfflineStorageText.Persistence(Desktop with { IosTab = true }), StringComparison.Ordinal);
        Assert.Contains("runs low", OfflineStorageText.Persistence(Desktop), StringComparison.Ordinal);
    }

    [Fact]
    public void WallNameUsesTheViewersWallAndNeverGuessesAStrangersName()
    {
        var id = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [id] = "The Attic" };

        Assert.Equal("The Attic", OfflineStorageText.WallName(Usage(id.ToString("D")), names));
        Assert.Equal("The Attic", OfflineStorageText.WallName(Usage(id.ToString("D").ToUpperInvariant()), names));
        Assert.Equal("A shared wall", OfflineStorageText.WallName(Usage(Guid.NewGuid().ToString("D")), names));
        Assert.Equal("A shared wall", OfflineStorageText.WallName(Usage("not-a-guid"), names));
    }

    [Fact]
    public void TheReportShapeFromJsDeserializesTheWayBlazorReadsIt()
    {
        const string fromJs = """
            {"caching":true,"kiosk":false,"persisted":true,"keep3d":false,"mobile":true,"background3d":false,
             "iosTab":true,"installHint":"In Safari: tap Share","paused":false,"total":30000,
             "walls":[{"wallId":"11111111-1111-1111-1111-111111111111","core":5000,"gallery":10000,"view3d":5000,
                       "other":0,"total":20000,"keepOffline":true,"saved3d":true,"ready":true,"lastOpened":1761050000000}]}
            """;

        var facts = JsonSerializer.Deserialize<OfflineStorageFacts>(fromJs, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(facts);
        Assert.False(facts.Keep3d);
        Assert.True(facts.IosTab);
        Assert.Equal(30000, facts.Total);
        var wall = Assert.Single(facts.Walls!);
        Assert.Equal(5000, wall.View3d);
        Assert.True(wall.KeepOffline);
        Assert.True(wall.Ready);
        Assert.True(wall.Saved3d);
        Assert.Equal(1761050000000, wall.LastOpened);
    }

    private static OfflineWallUsage Usage(string wallId) => new(wallId, 0, 0, 0, 0, 0, false, false, 0);
}
