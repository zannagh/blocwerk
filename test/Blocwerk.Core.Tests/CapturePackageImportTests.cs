// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The import side of a capture package: begin (dry run), upload (hash-verified), commit (rows in one transaction, the model
/// active, the trained view as a delivered GPU job nobody claimed, the capture queued). The pipeline then finishes the
/// view without training and runs the whole follow-up chain. The instance plays the target by forgetting the capture.
/// </summary>
public class CapturePackageImportTests
{
    [Fact]
    public async Task ACommittedImport_InsertsTheRows_AndThePipelineFinishesTheDeliveredView()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, bytes) = await f.ExportAsync();
        await f.ForgetAsync(manifest);

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        Assert.True(report.CanCommit, string.Join(" | ", report.Blockers));
        Assert.False(report.AlreadyImported);
        Assert.All(report.Files, x => Assert.Equal(CaptureImportFileState.Missing, x.State));
        await f.UploadAllAsync(manifest, bytes);
        var committed = await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None);

        Assert.True(committed.Committed, string.Join(" | ", committed.Blockers));
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            Assert.Equal(WallCaptureStatus.Splatting, capture.Status);
            Assert.Null(capture.FollowUpJson);
            Assert.Equal(2, await db.WallCapturePhotos.CountAsync(p => p.CaptureId == f.CaptureId));
            Assert.True((await db.WallGeometryModels.SingleAsync()).IsActive);
            var job = await db.GpuJobs.SingleAsync();
            Assert.Equal(GpuJobStatus.Succeeded, job.Status);
            Assert.Null(job.InstalledAt);
            Assert.Null(job.ClaimedByRunnerId);
            Assert.Equal(manifest.Rows.GpuJob.ResultPath, job.ResultPath);
        }

        await f.Runner.ProcessAsync();

        await using (var db = h.CreateContext())
        {
            var splat = await db.WallGeometrySplats.SingleAsync(s => s.GeometryModelId == manifest.Rows.Model.Id);
            Assert.NotNull((await db.GpuJobs.SingleAsync()).InstalledAt);
            Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
            await f.Runner.PhotoRealStep.Received(1).RunAsync(
                Arg.Is<CaptureFollowUpContext>(c => c.SplatId == splat.Id), Arg.Any<CancellationToken>());
        }

        Assert.Equal(2, f.Runner.Finishes());
        Assert.Single(f.Runner.Scenario.SplatClient.MultipartSubmissions, m => m.Kind == WallCaptureProcessor.PrepareKind);
    }

    [Fact]
    public async Task ACommit_WaitsForEveryFile_AndAWrongFileIsRejected()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, bytes) = await f.ExportAsync();
        await f.ForgetAsync(manifest);
        await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        var first = manifest.Files[0];

        await Assert.ThrowsAnyAsync<Services.UserFacingException>(() =>
            f.Service.PutFileAsync(f.CaptureId, first.Name, new MemoryStream([.. bytes[first.Name].Reverse()]), CancellationToken.None));
        await Assert.ThrowsAsync<Services.UserFacingException>(() =>
            f.Service.PutFileAsync(f.CaptureId, "0123456789abcdef0123456789abcdef.jpg", new MemoryStream([1]), CancellationToken.None));
        var early = await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None);

        Assert.False(early.Committed);
        Assert.Contains(early.Blockers, b => b.Contains("not uploaded"));
        await using var db = h.CreateContext();
        Assert.Empty(await db.WallCaptures.ToListAsync());
        Assert.Equal(CaptureImportFileState.Missing, (await f.Service.BeginImportAsync(manifest, CancellationToken.None)).Files[0].State);
    }

    [Fact]
    public async Task TheSameCaptureAgain_IsAlreadyImported_AndNothingIsUploadedOrInserted()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        var put = await f.Service.PutFileAsync(f.CaptureId, manifest.Files[0].Name, new MemoryStream([]), CancellationToken.None);
        var commit = await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None);

        Assert.True(report.AlreadyImported);
        Assert.Empty(report.Blockers);
        Assert.All(report.Files, x => Assert.Equal(CaptureImportFileState.Present, x.State));
        Assert.Equal(0, report.MissingBytes);
        Assert.Equal(CaptureImportFileState.Present, put.State);
        Assert.True(commit.AlreadyImported);
        Assert.False(commit.Committed);
        await using var db = h.CreateContext();
        Assert.Single(await db.WallCaptures.ToListAsync());
    }

    [Fact]
    public async Task IdsTakenByOtherRows_Block()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();
        await f.ForgetAsync(manifest, keepModel: true);

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);

        Assert.False(report.CanCommit);
        Assert.Contains(report.Blockers, b => b.Contains($"model {manifest.Rows.Model.Id}"));
    }

    [Fact]
    public async Task ANewerActiveModelOnTheTarget_Blocks()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();
        await f.ForgetAsync(manifest);
        await using (var db = h.CreateContext())
        {
            db.WallGeometryModels.Add(new WallGeometryModel
            {
                WallId = h.WallId, Json = manifest.Rows.Model.Json, Source = "newer", IsActive = true,
                CreatedAt = manifest.Rows.Model.CreatedAt.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);

        Assert.Contains(report.Blockers, b => b.Contains("newer active model"));
    }

    [Fact]
    public async Task NoSplatWorker_AMissingPlanRevision_AndADifferentFileWithTheSameName_Block()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();
        await f.ForgetAsync(manifest);
        f.Runner.Scenario.SplatClient.IsConfigured = false;
        var photo = manifest.Files.First(x => x.Role == "photo");
        await File.WriteAllBytesAsync(f.Runner.Scenario.Files.ResolvePhysicalPath(photo.Name)!, [1, 2, 3]);
        manifest.Rows.Capture.PlanRevision = 99;
        manifest = manifest with { PlanRevision = 99 };

        var report = await f.Service.BeginImportAsync(manifest, CancellationToken.None);

        Assert.Contains(report.Blockers, b => b.Contains("No splat worker"));
        Assert.Contains(report.Blockers, b => b.Contains("revision 99"));
        Assert.Contains(report.Blockers, b => b.Contains(photo.Name));
        Assert.Equal(CaptureImportFileState.Conflict, report.Files.Single(x => x.Name == photo.Name).State);
    }

    [Fact]
    public async Task APackageWhoseFileListIsNotItsRows_Blocks_AndTooLittleDiskBlocks()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();
        await f.ForgetAsync(manifest);

        var tampered = await f.Service.BeginImportAsync(manifest with { Files = manifest.Files.Skip(1).ToList() }, CancellationToken.None);
        f.Disk.Free = 10;
        var full = await f.Service.BeginImportAsync(manifest, CancellationToken.None);

        Assert.Contains(tampered.Blockers, b => b.Contains("file list"));
        Assert.Contains(full.Blockers, b => b.Contains("free"));
    }

    [Fact]
    public async Task ANonAdmin_CannotImport()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, _) = await f.ExportAsync();
        h.ActingUser = await h.AddMemberAsync("wall-admin", WallRole.Admin);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.BeginImportAsync(manifest, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None));
    }
}
