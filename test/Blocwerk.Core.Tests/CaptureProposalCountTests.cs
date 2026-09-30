// <copyright file="CaptureProposalCountTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture history's "possible new holds to review" line counts what the review list shows now: accepted,
/// rejected and hold-covered proposals drop out of the number stored when the search ran, the line goes away when
/// none are left, and a capture of an older model keeps its stored text.
/// </summary>
public class CaptureProposalCountTests
{
    private const string Placed = "856 holds placed on the 3D model";

    [Fact]
    public async Task ReviewedAndCoveredProposals_DropOutOfTheCount()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (captureId, modelId) = await SeedAsync(h, stored: 4);
        await ProposalAsync(h, modelId, 1000, 1000);
        await ProposalAsync(h, modelId, 2000, 1000);
        await ProposalAsync(h, modelId, 500, 500);
        await ProposalAsync(h, modelId, 800, 800, HoldProposalStatus.Accepted);
        await HoldAtAsync(h, 505, 498);

        var capture = Assert.Single(await s.Service.GetCapturesAsync(h.WallId));

        Assert.Equal(captureId, capture.Id);
        Assert.Equal($"{Placed}, 2 possible new holds to review.", capture.FollowUp);
    }

    [Fact]
    public async Task NoneLeftToReview_TheLineGoesAway()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (captureId, modelId) = await SeedAsync(h, stored: 1);
        await ProposalAsync(h, modelId, 500, 500);
        await HoldAtAsync(h, 500, 500);

        Assert.Equal($"{Placed}.", (await s.Service.GetCaptureAsync(captureId))!.FollowUp);
    }

    [Fact]
    public async Task ACaptureOfAnOlderModel_KeepsItsStoredText()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (captureId, modelId) = await SeedAsync(h, stored: 3);
        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync(m => m.Id == modelId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal($"{Placed}, 3 possible new holds to review.", (await s.Service.GetCaptureAsync(captureId))!.FollowUp);
    }

    [Theory]
    [InlineData(null, "3 possible new holds to review.")]
    [InlineData(1, "1 possible new hold to review.")]
    [InlineData(0, null)]
    public void Summary_UsesTheListedCount_WhenGiven(int? listed, string? expected)
    {
        var record = new CaptureFollowUpRecord([Entry(FindHoldProposalsFollowUpStep.Describe(3))]);

        Assert.True(CaptureFollowUpText.ReportsProposals(record));
        Assert.Equal(expected, CaptureFollowUpText.Summary(record, listed));
    }

    private static CaptureFollowUpEntry Entry(string summary, string key = FindHoldProposalsFollowUpStep.StepKey) =>
        new(key, CaptureFollowUpOutcome.Done, summary, DateTimeOffset.UtcNow);

    private static async Task<(Guid CaptureId, Guid ModelId)> SeedAsync(WallTestHarness h, int stored)
    {
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        var record = new CaptureFollowUpRecord([Entry(Placed, "place"), Entry(FindHoldProposalsFollowUpStep.Describe(stored))]);
        await using var db = h.CreateContext();
        (await db.WallCaptures.SingleAsync(c => c.Id == captureId)).FollowUpJson = record.ToJson();
        await db.SaveChangesAsync();
        return (captureId, modelId);
    }

    private static async Task ProposalAsync(
        WallTestHarness h, Guid modelId, double a, double b, HoldProposalStatus status = HoldProposalStatus.Pending)
    {
        await using var db = h.CreateContext();
        db.HoldProposals.Add(new HoldProposal
        {
            WallId = h.WallId, GeometryModelId = modelId, FacetId = "f1", A = a, B = b, H = 10, X = a, Y = -10, Z = b,
            SizeMm = 50, Views = 3, Confidence = 0.8, BestPhoto = "p.jpg", Status = status,
        });
        await db.SaveChangesAsync();
    }

    private static async Task HoldAtAsync(WallTestHarness h, double a, double b)
    {
        await using var db = h.CreateContext();
        db.Holds.Add(new Hold { WallId = h.WallId, X = 0.5, Y = 0.5, Radius = 0.02, FacetId = "f1", PlaneAMm = a, PlaneBMm = b });
        await db.SaveChangesAsync();
    }
}
