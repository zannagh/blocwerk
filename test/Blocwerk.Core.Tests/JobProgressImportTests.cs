// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Jobs;

namespace Blocwerk.Core.Tests;

/// <summary>An open capture import is a job: its upload progress is read from its staging folder.</summary>
public sealed class JobProgressImportTests : IDisposable
{
    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-job-import-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemCaptureFileStore files;

    public JobProgressImportTests()
    {
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storeDir;
        files = new FileSystemCaptureFileStore(settings);
    }

    [Fact]
    public async Task AnOpenImport_CountsStagedPartlyUploadedAndStoredFiles()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var importId = Guid.NewGuid();
        var folder = await StageAsync(importId, h.WallId, ("a.jpg", 100), ("b.jpg", 300), ("c.jpg", 600));
        await File.WriteAllBytesAsync(Path.Combine(folder, "a.jpg"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "b.jpg.part"), new byte[150]);
        await File.WriteAllBytesAsync(files.ResolvePhysicalPath("c.jpg")!, new byte[600]);
        var reader = new JobProgressReader(h.RootContextFactory, new CaptureImportProgress(files));

        var job = Assert.Single((await JobProgressReaderTests.ReadAsync(h, reader: reader)).Jobs);

        Assert.Equal(($"import:{importId}", JobKinds.Import, JobStates.Running, "uploading"), (job.Id, job.Kind, job.State, job.Stage));
        Assert.Equal((85.0, h.WallId), (job.Percent, job.WallId));
        Assert.Equal("850 of 1,000 bytes uploaded", job.Detail);
        Assert.False(string.IsNullOrEmpty(job.WallName));
    }

    [Fact]
    public async Task AnImport_IsOnlyInTheScopeOfItsWall_AndGoneOnceItsFolderIs()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var importId = Guid.NewGuid();
        var folder = await StageAsync(importId, h.WallId, ("a.jpg", 100));
        var reader = new JobProgressReader(h.RootContextFactory, new CaptureImportProgress(files), new MutableTestClock(JobProgressReaderTests.Now));

        var other = await reader.ReadAsync(new JobProgressScope(new HashSet<Guid> { Guid.NewGuid() }, TimeSpan.FromHours(1)), default);
        Directory.Delete(folder, recursive: true);
        var gone = await reader.ReadAsync(new JobProgressScope(null, TimeSpan.FromHours(1)), default);

        Assert.Empty(other.Jobs);
        Assert.Empty(gone.Jobs);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(storeDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }

    private async Task<string> StageAsync(Guid importId, Guid wallId, params (string Name, long Bytes)[] packageFiles)
    {
        var root = Path.GetDirectoryName(files.ResolvePhysicalPath("probe.bin"))!;
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "imports", importId.ToString("N"));
        Directory.CreateDirectory(folder);
        var list = string.Join(",", packageFiles.Select(f => $"{{\"name\":\"{f.Name}\",\"bytes\":{f.Bytes},\"sha256\":\"{new string('0', 64)}\",\"role\":\"photo\"}}"));
        var manifest = $"{{\"formatVersion\":1,\"captureId\":\"{importId}\",\"wallId\":\"{wallId}\",\"rows\":{{}},\"files\":[{list}]}}";
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), manifest);
        return folder;
    }
}
