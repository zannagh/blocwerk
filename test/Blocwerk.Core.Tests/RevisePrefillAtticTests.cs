using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.RealData;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The Revise pre-selection replayed on The Attic's real gen-5 to gen-6 lineage (a read-only copy of prod,
/// 2026-10-20), for every boulder the bug hit: the three revised that evening (their PRE-revise marks, from the
/// incident report's lineage tables) and the seven still frozen (expected sets from the report's independent
/// simulation). Before the fix, 6/7 to 10/10 of each boulder's pre-ticks were a neighbour, some on panel (1,0).
/// </summary>
public class RevisePrefillAtticTests
{
    private static readonly Lazy<Fixture> Data = new(Load);

    public static TheoryData<string> Boulders()
    {
        var data = new TheoryData<string>();
        foreach (var boulder in Data.Value.Boulders)
        {
            data.Add(boulder.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Boulders))]
    public void PreTicks_AreExactlyTheLineageSet_WithTheBouldersOwnMarks(string name)
    {
        var data = Data.Value;
        var boulder = data.Boulders.Single(b => b.Name == name);

        var result = RevisePrefillMapper.Map(boulder.Marks, data.Lineage, data.LiveHolds, data.Positions, data.LivePanels);

        var expected = boulder.Expected
            .GroupBy(e => e.HoldId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Type).ThenBy(e => e.Usage).First());
        Assert.Equal(expected.Keys.Order(), result.Picks.Select(p => p.HoldId).Order());
        Assert.All(result.Picks, p =>
        {
            Assert.Equal(expected[p.HoldId].Type, p.Type);
            Assert.Equal(expected[p.HoldId].Usage, p.Usage);
            Assert.NotEqual(RevisePickSource.Guessed, p.Source);
        });
        Assert.Equal(boulder.Unmapped.Order(), result.UnmappedHoldIds.Order());
    }

    [Fact]
    public void TicTacPinch_OpensOnTheCentrePanel_WithAllTenHoldsThere()
    {
        var data = Data.Value;
        var boulder = data.Boulders.Single(b => b.Name == "Tic tac pinch");

        var result = RevisePrefillMapper.Map(boulder.Marks, data.Lineage, data.LiveHolds, data.Positions, data.LivePanels);

        var centre = data.LivePanels.Single(id => data.Positions[id] == (0, 0));
        Assert.Equal(centre, result.StartPanelId);
        Assert.Equal(10, result.Picks.Count);
        Assert.All(result.Picks, p => Assert.Equal(centre, data.LiveHolds.Single(h => h.Id == p.HoldId).WallPanelId));
    }

    private static Fixture Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AtticRealData.Dir, "revise-lineage.json")));
        var root = doc.RootElement;
        var holds = root.GetProperty("holds").EnumerateArray().ToDictionary(
            h => h.GetProperty("id").GetGuid(),
            h => new Hold
            {
                Id = h.GetProperty("id").GetGuid(),
                WallPanelId = h.GetProperty("panel").ValueKind == JsonValueKind.Null ? null : h.GetProperty("panel").GetGuid(),
                X = h.GetProperty("x").GetDouble(),
                Y = h.GetProperty("y").GetDouble(),
                IsVirtual = h.GetProperty("virtual").GetBoolean(),
                Generation = h.GetProperty("generation").GetInt32(),
            });
        var live = root.GetProperty("liveHoldIds").EnumerateArray().Select(e => holds[e.GetGuid()]).ToList();
        var lineage = root.GetProperty("links").EnumerateArray().Select(l => new ReviseLineageStep(
            NullableGuid(l.GetProperty("old")),
            NullableGuid(l.GetProperty("new")),
            (HoldGenerationLinkKind)l.GetProperty("kind").GetInt32(),
            l.GetProperty("fromGeneration").GetInt32(),
            l.GetProperty("toGeneration").GetInt32())).ToList();
        var positions = root.GetProperty("panels").EnumerateArray().ToDictionary(
            p => p.GetProperty("id").GetGuid(),
            p => (p.GetProperty("col").GetInt32(), p.GetProperty("row").GetInt32()));
        var livePanels = root.GetProperty("livePanels").EnumerateArray().Select(e => e.GetGuid()).ToList();
        var boulders = root.GetProperty("boulders").EnumerateArray().Select(b => new FixtureBoulder(
            b.GetProperty("name").GetString()!.Trim().Split(' ').Where(w => w.All(c => c < 0x2000)).Aggregate((x, y) => x + " " + y),
            b.GetProperty("marks").EnumerateArray().Select(m =>
            {
                var hold = holds[m.GetProperty("hold").GetGuid()];
                return new ReviseMark(hold.Id, (HoldType)m.GetProperty("type").GetInt32(), (HoldUsage)m.GetProperty("usage").GetInt32(), hold.WallPanelId, hold.X, hold.Y);
            }).ToList(),
            b.GetProperty("expected").EnumerateArray().Select(e => new BoulderHoldInput(
                e.GetProperty("hold").GetGuid(),
                (HoldType)e.GetProperty("type").GetInt32(),
                (HoldUsage)e.GetProperty("usage").GetInt32())).ToList(),
            b.GetProperty("unmapped").EnumerateArray().Select(e => e.GetGuid()).ToList())).ToList();
        return new Fixture(live, lineage, positions, livePanels, boulders);
    }

    private static Guid? NullableGuid(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetGuid();

    private sealed record Fixture(
        List<Hold> LiveHolds,
        List<ReviseLineageStep> Lineage,
        Dictionary<Guid, (int Col, int Row)> Positions,
        List<Guid> LivePanels,
        List<FixtureBoulder> Boulders);

    private sealed record FixtureBoulder(string Name, List<ReviseMark> Marks, List<BoulderHoldInput> Expected, List<Guid> Unmapped);
}
