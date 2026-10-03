// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// While a finished capture's model is solved again or its wall textures are rendered again, the wall's active model must
/// not be replaced: corrections and new captures are refused with a message saying why.
/// </summary>
public class CaptureRedoBusyTests
{
    private static readonly CaptureScaleReference TenPercent = new(1, [1000, 750], [1600, 750], 1980);

    [Theory]
    [InlineData("resolve:", null)]
    [InlineData("resolve:job-1", null)]
    [InlineData(null, "rerender:")]
    [InlineData(null, "rerender:job-2")]
    public async Task ACorrection_IsRefused_WhileTheModelIsRedone(string? solveJobId, string? texturesJobId)
    {
        using var h = new WallTestHarness();
        var (modelId, captureId) = await GeometryCorrectionFixture.SeedAsync(h);
        await MarkAsync(h, captureId, solveJobId, texturesJobId);

        var ex = await Assert.ThrowsAsync<UserFacingException>(
            () => GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue()).MakeSizesExactAsync(h.WallId, TenPercent));

        Assert.StartsWith(CaptureRedoMarks.BusyMessage, ex.Message, StringComparison.Ordinal);
        await using var db = h.CreateContext();
        Assert.Equal(modelId, (await db.WallGeometryModels.SingleAsync(m => m.IsActive)).Id);
    }

    [Fact]
    public async Task ACorrection_RunsOnceTheRedoIsDone()
    {
        using var h = new WallTestHarness();
        var (modelId, captureId) = await GeometryCorrectionFixture.SeedAsync(h);
        await MarkAsync(h, captureId, "job-1", "job-2");

        var result = await GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue()).MakeSizesExactAsync(h.WallId, TenPercent);

        Assert.Equal(modelId, result.PreviousModelId);
    }

    [Fact]
    public async Task ANewCapture_IsRefused_WhileAnotherCapturesModelIsRedone()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        for (var i = 0; i < 2; i++)
        {
            await s.Service.AddPhotoAsync(draft.CaptureId, $"IMG_{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: i)), CancellationToken.None);
        }

        await using (var db = h.CreateContext())
        {
            db.WallCaptures.Add(new WallCapture
            {
                WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, TexturesJobId = "rerender:",
            });
            await db.SaveChangesAsync();
        }

        var declarations = await s.Service.SuggestDeclarationsAsync(draft.CaptureId);
        var problems = await s.Service.StartAsync(draft.CaptureId, declarations, "second capture");

        Assert.Contains(problems, p => p.StartsWith(CaptureRedoMarks.BusyMessage, StringComparison.Ordinal));
        await using var read = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Draft, (await read.WallCaptures.SingleAsync(c => c.Id == draft.CaptureId)).Status);
    }

    [Fact]
    public async Task ActivatingAnOlderModel_AndAManualImport_AreRefused_WhileTheModelIsRedone()
    {
        using var h = new WallTestHarness();
        var (modelId, captureId) = await GeometryCorrectionFixture.SeedAsync(h, json: GlyphGeometryJson.Build());
        var glyphs = WallGlyphSettingsTests.Service(h);
        Guid olderId;
        await using (var db = h.CreateContext())
        {
            var older = new WallGeometryModel { WallId = h.WallId, Json = GlyphGeometryJson.Build(), SchemaVersion = 1, Source = "older", IsActive = false };
            db.WallGeometryModels.Add(older);
            await db.SaveChangesAsync();
            olderId = older.Id;
        }

        await MarkAsync(h, captureId, CaptureResolveMark.Mark, null);

        var activate = await Assert.ThrowsAsync<UserFacingException>(() => glyphs.ActivateGeometryAsync(olderId));
        Assert.StartsWith(CaptureRedoMarks.BusyMessage, activate.Message, StringComparison.Ordinal);
        var import = await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), "manual");
        Assert.False(import.Succeeded);
        Assert.StartsWith(CaptureRedoMarks.BusyMessage, Assert.Single(import.Errors), StringComparison.Ordinal);
        await using var read = h.CreateContext();
        Assert.Equal(modelId, (await read.WallGeometryModels.SingleAsync(m => m.IsActive)).Id);
    }

    [Fact]
    public async Task APackageImport_IsBlocked_WhileTheWallsModelIsRedone()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();
        await f.ForgetAsync(manifest, keepModel: true);
        await using (var db = h.CreateContext())
        {
            db.WallCaptures.Add(new WallCapture
            {
                WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, TexturesJobId = CaptureTextureOutcome.RerenderMark,
            });
            await db.SaveChangesAsync();
        }

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);

        Assert.Contains(report.Blockers, b => b.StartsWith(CaptureRedoMarks.BusyMessage, StringComparison.Ordinal));
    }

    private static async Task MarkAsync(WallTestHarness h, Guid captureId, string? solveJobId, string? texturesJobId)
    {
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        capture.SolveJobId = solveJobId;
        capture.TexturesJobId = texturesJobId;
        await db.SaveChangesAsync();
    }
}
