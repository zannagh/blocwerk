// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A capture package of a capture with history on its source, replayed onto a target that has none of it (prod before its
/// first 3D model): the model was solved again after training (the view's GPU job belongs to the first model, whose view
/// the re-solved model shares), it is registered to an earlier model, and the wall's plan has another revision number on
/// the target than on the source.
/// </summary>
public class CapturePackageLineageTests
{
    private const string Plan = """{"version":1,"dictionary":"DICT_4X4_50","markers":[{"id":1,"sizeMm":100}]}""";

    [Fact]
    public async Task AReSolvedModel_ExportsWithTheViewBoundToIt()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (first, resolved) = await ReSolveAsync(h, f.CaptureId, shareView: true);

        var (manifest, _) = await f.ExportAsync();

        Assert.Equal(resolved, manifest.Rows.Model.Id);
        Assert.Equal(resolved, manifest.Rows.GpuJob.GeometryModelId);
        Assert.Contains(manifest.Warnings, w => w.Contains($"trained for model {first}"));
    }

    [Fact]
    public async Task AReSolvedModelWithoutTheSharedView_IsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        await ReSolveAsync(h, f.CaptureId, shareView: false);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => f.Service.ExportAsync(f.CaptureId, CancellationToken.None));

        Assert.Contains("does not share that view", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReSolvedRegisteredModel_OnATargetWithoutItsHistory_BecomesTheFirstModel_WithTheTargetsPlanRevision(bool jobNamesTheFirstModel)
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (first, resolved) = await ReSolveAsync(h, f.CaptureId, shareView: true);
        var (manifest, bytes) = await f.ExportAsync();
        manifest = WithHistory(manifest, first, sourceRevision: 2);
        if (jobNamesTheFirstModel)
        {
            // As a source without this fix exports it: the view's job still names the model it was trained for.
            manifest.Rows.GpuJob.GeometryModelId = first;
        }

        await BecomeEmptyTargetAsync(h, f, manifest, targetRevision: 1);

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        Assert.True(report.CanCommit, string.Join(" | ", report.Blockers));
        Assert.Contains(report.Warnings, w => w.Contains("is revision 1 here"));
        Assert.Contains(report.Warnings, w => w.Contains($"registered to model {first}"));
        await f.UploadAllAsync(manifest, bytes);
        var committed = await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None);
        Assert.True(committed.Committed, string.Join(" | ", committed.Blockers));
        await f.Runner.ProcessAsync();

        await using var db = h.CreateContext();
        var model = await db.WallGeometryModels.SingleAsync();
        Assert.Equal(resolved, model.Id);
        Assert.True(model.IsActive);
        Assert.Equal(1, model.PlanRevision);
        Assert.Equal(1, (await db.WallCaptures.SingleAsync()).PlanRevision);
        Assert.Null(RegisteredGeometry.Carried(model.Json).ReferenceModelId);
        Assert.False(FrameLineage.IsReset(model.Json));
        Assert.Equal(first.ToString(), JsonNode.Parse(model.Json)!["quality"]![RegisteredGeometry.HistoryKey]!["referenceModelId"]!.GetValue<string>());
        Assert.Equal(resolved, (await db.GpuJobs.SingleAsync()).GeometryModelId);
        Assert.NotNull((await db.GpuJobs.SingleAsync()).InstalledAt);
        Assert.Equal(resolved, (await db.WallGeometrySplats.SingleAsync()).GeometryModelId);
        Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
    }

    [Fact]
    public async Task AJobOfAnotherModel_WhoseModelIsNotAReSolveOfTheCapture_Blocks()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();
        await f.ForgetAsync(manifest);
        manifest.Rows.GpuJob.GeometryModelId = Guid.NewGuid();

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);

        Assert.Contains(report.Blockers, b => b.Contains("GPU job row"));
    }

    [Fact]
    public async Task APlanNeitherNumberedNorWrittenAlikeOnTheTarget_Blocks()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();
        manifest = WithHistory(manifest, Guid.NewGuid(), sourceRevision: 2);
        await BecomeEmptyTargetAsync(h, f, manifest, targetRevision: 1, planJson: Plan.Replace("100", "125"));

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);

        Assert.Contains(report.Blockers, b => b.Contains("revision 2") && b.Contains("same plan"));
    }

    /// <summary>A second model of the capture, active, registered like a re-solve; the view shared with it on request.</summary>
    private static async Task<(Guid First, Guid Resolved)> ReSolveAsync(WallTestHarness h, Guid captureId, bool shareView)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        var first = await db.WallGeometryModels.SingleAsync(m => m.Id == capture.GeometryModelId);
        first.IsActive = false;
        await db.SaveChangesAsync();
        var resolved = new WallGeometryModel
        {
            WallId = first.WallId, Json = Registered(first.Json, first.Id), SchemaVersion = first.SchemaVersion,
            Source = $"capture {captureId:N} re-solve", CreatedAt = first.CreatedAt.AddMinutes(5), IsActive = true,
            FrameSource = first.FrameSource,
        };
        db.WallGeometryModels.Add(resolved);
        capture.GeometryModelId = resolved.Id;
        if (shareView)
        {
            var view = await db.WallGeometrySplats.AsNoTracking().SingleAsync(s => s.GeometryModelId == first.Id);
            db.WallGeometrySplats.Add(new WallGeometrySplat
            {
                GeometryModelId = resolved.Id, StoredPath = view.StoredPath, SizeBytes = view.SizeBytes, FrameJson = view.FrameJson,
            });
        }

        await db.SaveChangesAsync();
        return (first.Id, resolved.Id);
    }

    private static string Registered(string json, Guid reference)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (root["quality"] is not JsonObject quality)
        {
            quality = [];
            root["quality"] = quality;
        }

        quality["registration"] = new JsonObject { ["referenceModelId"] = reference.ToString(), ["carriedFacets"] = new JsonArray() };
        return root.ToJsonString();
    }

    /// <summary>The package as a source with a plan revision and a registration to <paramref name="reference"/> writes it.</summary>
    private static CapturePackageManifest WithHistory(CapturePackageManifest m, Guid reference, int sourceRevision)
    {
        m.Rows.Capture.PlanRevision = sourceRevision;
        m.Rows.Capture.PlanJson = Plan;
        m.Rows.Model.PlanRevision = sourceRevision;
        m.Rows.Model.Json = Registered(m.Rows.Model.Json, reference);
        return m with { PlanRevision = sourceRevision, PlanJson = Plan, ReferenceModelId = reference };
    }

    /// <summary>The instance forgets the capture and every model of the wall, and has the plan as its own revision.</summary>
    private static async Task BecomeEmptyTargetAsync(
        WallTestHarness h, CapturePackageFlow f, CapturePackageManifest manifest, int targetRevision, string planJson = Plan)
    {
        await f.ForgetAsync(manifest);
        await using var db = h.CreateContext();
        await db.WallGeometryModels.Where(m => m.WallId == h.WallId).ExecuteDeleteAsync();
        await db.WallMarkerPlans.Where(p => p.WallId == h.WallId).ExecuteDeleteAsync();
        db.WallMarkerPlans.Add(new WallMarkerPlan { WallId = h.WallId, Json = planJson, Revision = targetRevision, IsCurrent = true });
        await db.SaveChangesAsync();
    }
}
