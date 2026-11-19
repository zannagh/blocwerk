// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Client.Views;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Helpers;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests;

/// <summary>How the boulder lists fold a family into its parent's row, on the Server and in the offline client.</summary>
public class BoulderFamiliesTests
{
    private static readonly Guid Parent = Guid.NewGuid();

    [Theory]
    [InlineData("6A,7A,6B", "6A–7A")]
    [InlineData("6A,6A", "6A")]
    [InlineData("7A,,6C", "6C–7A")]
    [InlineData("V3,6A", "6A–V3")]
    [InlineData(",", null)]
    public void TheRange_RunsFromLowestToHighest(string grades, string? expected)
    {
        Assert.Equal(expected, BoulderFamilies.GradeRange(grades.Split(',').Select(g => g.Length == 0 ? null : g)));
    }

    [Fact]
    public void APublishedVariant_IsHiddenUnderItsParent_AndCounted()
    {
        var variant = Member("Hard", "7A", Parent);

        var rows = BoulderFamilies.Rows([Member("Parent", "6A", null, Parent), variant]);

        var row = Assert.Single(rows).Value;
        Assert.Equal((1, "6A–7A", GradeScoring.GetScore("6A", false)), (row.VariantCount, row.GradeRange, row.LowestScore));
        Assert.Equal(["Hard"], row.VariantNames);
    }

    [Fact]
    public void ADraftVariant_KeepsItsOwnRow_AndNeverCounts()
    {
        var draft = Member("Draft", "8A", Parent) with { IsDraft = true };

        var rows = BoulderFamilies.Rows([Member("Parent", "6A", null, Parent), draft]);

        Assert.Equal(2, rows.Count);
        Assert.Equal((0, "6A"), (rows[Parent].VariantCount, rows[Parent].GradeRange));
    }

    [Fact]
    public void AnArchivedVariant_StaysInTheArchive_AndAVariantWhoseParentIsUnseenIsListed()
    {
        var archived = Member("Archived", "8A", Parent) with { IsArchived = true };
        var orphan = Member("Orphan", "6B", Guid.NewGuid());

        var rows = BoulderFamilies.Rows([Member("Parent", "6A", null, Parent), archived, orphan]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[Parent].VariantCount);
    }

    [Fact]
    public void TheOfflineList_ShowsOnlyTheParent_AndFindsItByItsFamily()
    {
        IReadOnlyList<BouldersDocumentBoulder> boulders =
        [
            CrimpLine() with { Id = Parent, Name = "Parent", Grade = "6A" },
            CrimpLine() with { Id = Guid.NewGuid(), Name = "Hidden Traverse", Grade = "7B", ParentBoulderId = Parent },
            CrimpLine() with { Id = Guid.NewGuid(), Name = "Middle", Grade = "6C" },
        ];

        Assert.Equal(["Parent", "Middle"], Names(boulders, new BoulderFilter()));
        Assert.Equal(["Parent"], Names(boulders, new BoulderFilter { Search = "traverse" }));
        Assert.Equal(["Parent"], Names(boulders, new BoulderFilter { GradeMin = "7A" }));
        Assert.Equal(["Parent", "Middle"], Names(boulders, new BoulderFilter { Sort = BoulderSort.GradeAsc }));
        Assert.Equal("6A–7B", BoulderList.Families(boulders)[Parent].GradeRange);
    }

    private static IEnumerable<string> Names(IReadOnlyList<BouldersDocumentBoulder> boulders, BoulderFilter filter) =>
        BoulderList.Apply(boulders, MineMarks.None, filter).Active.Select(b => b.Name);

    private static BoulderFamilyMember Member(string name, string? grade, Guid? parent, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), parent, name, grade, false, false);
}
