// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.MarkerRevisions.RegistrationFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Re-solve 3D model (no retraining)": a finished capture's model solved again from its kept photos, registered to the
/// active model and activated only when that holds. The photo-real view is kept (no training, no GPU job); textures and
/// follow-ups are redone for the new model.
/// </summary>
public class WallCaptureModelResolveTests
{
    [Fact]
    public async Task Resolve_ActivatesTheRegisteredModel_KeepsTheView_AndRedoesTexturesAndFollowUps()
    {
        using var h = new WallTestHarness();
        var log = new List<string>();
        using var s = new CaptureScenario(h, followUps: harness => FollowUpChains.Build(
            harness.RootContextFactory,
            new ScriptedFollowUpStep(PlaceHoldsFollowUpStep.StepKey, 100, log),
            new ScriptedFollowUpStep("volumes", 250, log),
            new ScriptedFollowUpStep("measure", 300, log, needsPhotoReal: true)));
        s.SplatClient.IsConfigured = true;
        s.Client.GeometryJson = Solved(Rev1Json);
        var captureId = await FinishedAsync(s);
        var before = await CaptureAsync(h);
        var splatJobs = s.SplatClient.MultipartSubmissions.Count;
        log.Clear();
        Assert.True((await s.Service.GetCaptureAsync(captureId))!.CanResolveModel);

        Assert.Empty(await s.Service.ResolveModelAsync(captureId));
        Assert.True((await s.Service.GetCaptureAsync(captureId))!.ModelResolving);
        Assert.Equal(captureId, await s.ResolveQueue.DequeueAsync(CancellationToken.None));
        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        var after = await CaptureAsync(h);
        await using var db = h.CreateContext();
        var active = await db.WallGeometryModels.SingleAsync(m => m.WallId == h.WallId && m.IsActive);
        Assert.NotEqual(before.GeometryModelId, active.Id);
        Assert.Equal(active.Id, after.GeometryModelId);
        Assert.Equal(WallCaptureProcessor.ResolvedModelSource(captureId), active.Source);
        Assert.NotNull(JsonNode.Parse(active.Json)!["quality"]!["registration"]);
        Assert.Equal((before.Status, before.SplatJobId), (after.Status, after.SplatJobId));
        Assert.False(CaptureResolveMark.IsResolving(after.SolveJobId));
        Assert.False(CaptureTextureOutcome.IsRerendering(after.TexturesJobId));

        var views = await db.WallGeometrySplats.AsNoTracking().ToListAsync();
        Assert.Equal(2, views.Count);
        Assert.Equal(views[0].StoredPath, views[1].StoredPath);
        Assert.Equal(views[0].FrameJson, views[1].FrameJson);
        Assert.Contains(views, v => v.GeometryModelId == active.Id);
        Assert.Equal(splatJobs, s.SplatClient.MultipartSubmissions.Count);
        Assert.Empty(await db.GpuJobs.ToListAsync());
        Assert.True(await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == active.Id));
        Assert.Equal([PlaceHoldsFollowUpStep.StepKey, "volumes", "measure"], log);
        Assert.Contains("solved again", (await s.Service.GetCaptureAsync(captureId))!.FollowUpNote);
    }

    [Fact]
    public async Task Resolve_ThatDoesNotRegister_IsStored_AndTheOldModelStaysActive()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.Client.GeometryJson = Solved(Rev1Json);
        var captureId = await FinishedAsync(s);
        var before = await CaptureAsync(h);
        s.Client.GeometryJson = CaptureScenario.GeometryWithCameras("p00", "p01");

        Assert.Empty(await s.Service.ResolveModelAsync(captureId));
        await s.Processor.ResolveModelAsync(captureId, CancellationToken.None);

        var after = await CaptureAsync(h);
        await using var db = h.CreateContext();
        var models = await db.WallGeometryModels.Where(m => m.WallId == h.WallId).ToListAsync();
        Assert.Equal(2, models.Count);
        Assert.Equal(before.GeometryModelId, models.Single(m => m.IsActive).Id);
        Assert.Equal(WallCaptureProcessor.ResolvedModelSource(captureId), models.Single(m => !m.IsActive).Source);
        Assert.Equal((before.GeometryModelId, before.Status), (after.GeometryModelId, after.Status));
        Assert.False(CaptureResolveMark.IsResolving(after.SolveJobId));
        Assert.Contains("NOT activated", (await s.Service.GetCaptureAsync(captureId))!.FollowUpNote);
        Assert.Single(await db.WallGeometrySplats.ToListAsync());
    }

    [Fact]
    public async Task Resolve_IsRefused_ForANonAdmin_AnInactiveModel_AndDeletedPhotos()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await FinishedAsync(s);
        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Contains("This capture's 3D model is not the wall's active one.", await s.Service.ResolveModelAsync(captureId));
        Assert.False((await s.Service.GetCaptureAsync(captureId))!.CanResolveModel);

        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync()).IsActive = true;
            db.WallCapturePhotos.RemoveRange(await db.WallCapturePhotos.ToListAsync());
            await db.SaveChangesAsync();
        }

        Assert.Contains(await s.Service.ResolveModelAsync(captureId), p => p.StartsWith("This capture's photos were already deleted", StringComparison.Ordinal));
        Assert.False((await s.Service.GetCaptureAsync(captureId))!.CanResolveModel);

        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.ResolveModelAsync(captureId));
        Assert.False(CaptureResolveMark.IsResolving((await CaptureAsync(h)).SolveJobId));
    }

    /// <summary>A started and processed capture; the start's entry is taken off the capture queue.</summary>
    private static async Task<Guid> FinishedAsync(CaptureScenario s)
    {
        var captureId = await s.StartCaptureAsync();
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        return captureId;
    }

    private static async Task<WallCapture> CaptureAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.AsNoTracking().SingleAsync();
    }

    /// <summary>A solve result whose cameras are named like the capture's photos (p00, p01, …).</summary>
    private static string Solved(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var cameras = root["cameras"]!.AsArray();
        for (var i = 0; i < cameras.Count; i++)
        {
            cameras[i]!["image"] = $"p{i:D2}";
        }

        return root.ToJsonString();
    }
}
