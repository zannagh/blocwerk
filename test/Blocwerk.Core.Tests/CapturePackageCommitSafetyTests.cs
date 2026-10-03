// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A commit whose move into the store fails part-way moves the files it already moved back, and concurrent uploads or
/// commits of one import run one at a time instead of tripping over each other's files.
/// </summary>
public class CapturePackageCommitSafetyTests
{
    [Fact]
    public async Task AMoveThatFailsPartWay_PutsTheMovedFilesBackIntoStaging()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, bytes) = await f.ExportAsync();
        await f.ForgetAsync(manifest);
        await f.Service.BeginImportAsync(manifest, CancellationToken.None);
        await f.UploadAllAsync(manifest, bytes);
        var staging = new CapturePackageStaging(f.Runner.Scenario.Files);
        var (first, second) = (manifest.Files[0].Name, manifest.Files[1].Name);

        // A directory in the way of the second file: the check sees nothing there, the move fails.
        Directory.CreateDirectory(staging.StorePath(second));
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None));

            Assert.True(File.Exists(staging.StagedPath(f.CaptureId, first)));
            Assert.False(File.Exists(staging.StorePath(first)));
            await using var db = h.CreateContext();
            Assert.Empty(await db.WallCaptures.ToListAsync());
        }
        finally
        {
            Directory.Delete(staging.StorePath(second));
        }

        Assert.True((await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None)).Committed);
    }

    [Fact]
    public async Task ConcurrentUploadsAndCommits_OfOneImport_DoNotCollide()
    {
        using var h = new WallTestHarness();
        using var f = await CapturePackageFlow.FinishedAsync(h);
        var (manifest, bytes) = await f.ExportAsync();
        await f.ForgetAsync(manifest);
        await f.Service.BeginImportAsync(manifest, CancellationToken.None);

        var puts = manifest.Files.SelectMany(file => Enumerable.Range(0, 2).Select(_ =>
            Task.Run(() => f.Service.PutFileAsync(f.CaptureId, file.Name, new MemoryStream(bytes[file.Name]), CancellationToken.None))));
        var states = await Task.WhenAll(puts);
        Assert.All(states, s => Assert.NotEqual(CaptureImportFileState.Missing, s.State));

        var commits = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try
            {
                return await f.Service.CommitImportAsync(f.CaptureId, CancellationToken.None);
            }
            catch (UserFacingException)
            {
                // The other commit finished first and closed the import.
                return null;
            }
        })));

        Assert.Single(commits, c => c?.Committed == true);
        await using var db = h.CreateContext();
        Assert.Single(await db.WallCaptures.ToListAsync());
    }
}
