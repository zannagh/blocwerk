// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Building the envelope of a large capture costs the app no memory of the bundle's size: every file part is copied from disk
/// into the stored zip and hashed in chunks, never held whole (the prod budget: well under 100 MiB for the Attic's photos).
/// </summary>
[Collection(AllocationMeasureCollection.Name)]
public class RunnerSplatMemoryTests
{
    private const long PartBytes = 192L * 1024 * 1024;
    private const long AllowedAllocation = 32L * 1024 * 1024;

    [Fact]
    public async Task SubmittingA192MbPrepare_AllocatesFarLessThanTheFilesItSends()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, RunnerSplatFlowTests.OnRunners());
        var client = new RunnerSplatClient(f.Queue, f.Files, NullLogger<RunnerSplatClient>.Instance);
        var owner = new ComputeJobOwner(h.WallId, await CaptureAsync(h), null);
        var dir = Directory.CreateTempSubdirectory("blocwerk-envelope-memory");
        try
        {
            var parts = new List<ComputeJobPart> { ComputeJobPart.Json("options", "{}") };
            for (var i = 0; i < 4; i++)
            {
                var path = Path.Combine(dir.FullName, $"p{i:00}.jpg");
                await using (var file = File.Create(path))
                {
                    file.SetLength(PartBytes / 4);
                }

                parts.Add(ComputeJobPart.FromDisk("photos", $"p{i:00}.jpg", path, "image/jpeg"));
            }

            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var id = await client.SubmitMultipartAsync(WallCaptureProcessor.PrepareKind, parts, owner, CancellationToken.None);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            var job = await SplatJobSupport.JobAsync(h, Guid.Parse(id[RunnerSplatClient.IdPrefix.Length..]));
            Assert.True(job.BundleBytes > PartBytes, $"the envelope holds the parts ({job.BundleBytes} bytes)");
            Assert.True(allocated < AllowedAllocation, $"building a {PartBytes >> 20} MB envelope allocated {allocated >> 20} MB");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task<Guid> CaptureAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var capture = new WallCapture { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Solving };
        db.WallCaptures.Add(capture);
        await db.SaveChangesAsync();
        return capture.Id;
    }
}
