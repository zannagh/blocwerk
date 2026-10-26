// <copyright file="MarkerIdPolicyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// Id 17 (one 2×2 white block, read off dark holds with a bright bolt) is never issued; 41 and 37 only when every
/// other id is taken; a plan that still has 17 is told to re-number it, without being refused.
/// </summary>
public class MarkerIdPolicyTests
{
    [Fact]
    public void TheIssueOrder_SkipsTheRetiredId_AndEndsWithTheIdsHoldsAlsoReadAs()
    {
        var order = MarkerIdPolicy.IssueOrder;

        Assert.Equal(ArucoDict4X4.Count - 1, order.Count);
        Assert.DoesNotContain(17, order);
        Assert.Equal([.. Enumerable.Range(0, 17), 18, 19], order.Take(19));
        Assert.Equal([41, 37], order.TakeLast(2));
        Assert.Equal(order.Count, order.Distinct().Count());
    }

    [Fact]
    public void TheRetiredIdIsTheOnlyPayloadThatIsOneBrightBlockOnDark()
    {
        static int White(int id) => Enumerable.Range(1, 4).Sum(r => Enumerable.Range(1, 4).Count(c => ArucoDict4X4.IsWhite(id, r, c)));

        var sparsest = Enumerable.Range(0, ArucoDict4X4.Count).MinBy(White);

        Assert.Equal(17, sparsest);
        Assert.Equal(4, White(17));
        Assert.True(ArucoDict4X4.IsWhite(17, 1, 2) && ArucoDict4X4.IsWhite(17, 1, 3) && ArucoDict4X4.IsWhite(17, 2, 2) && ArucoDict4X4.IsWhite(17, 2, 3));
        Assert.Equal([17], MarkerIdPolicy.Retired);
    }

    [Fact]
    public void NextFree_SkipsTheRetiredId_AndRunsPastTheDictionaryWhenEveryIdIsTaken()
    {
        Assert.Equal(0, MarkerIdPolicy.NextFree([]));
        Assert.Equal(18, MarkerIdPolicy.NextFree(Enumerable.Range(0, 17)));
        Assert.Equal(18, MarkerIdPolicy.NextFree([.. Enumerable.Range(0, 17), 17]));
        Assert.Equal(41, MarkerIdPolicy.NextFree(MarkerIdPolicy.IssueOrder.SkipLast(2)));
        Assert.Equal(ArucoDict4X4.Count, MarkerIdPolicy.NextFree(MarkerIdPolicy.IssueOrder));
        Assert.Equal(ArucoDict4X4.Count + 1, MarkerIdPolicy.IdAt(MarkerIdPolicy.IssueOrder.Count + 1));
    }

    [Fact]
    public void APlanThatStillHasTheRetiredId_IsWarned_NotRefused()
    {
        var plan = AtticMarkerPlan.Plan with
        {
            Markers = AtticMarkerPlan.Plan.Markers.Select(m => m.Id == 14 ? m with { Id = 17 } : m).ToList(),
        };

        var issues = MarkerPlanValidator.Validate(plan);

        var retired = Assert.Single(issues, i => i.Code == "marker-id-retired");
        Assert.Equal(PlanIssueSeverity.Warning, retired.Severity);
        Assert.Contains("Id 17 is retired", retired.Message);
        Assert.DoesNotContain(issues, i => i.Severity == PlanIssueSeverity.Error);
        Assert.DoesNotContain(MarkerPlanValidator.Validate(AtticMarkerPlan.Plan), i => i.Code == "marker-id-retired");
    }
}
