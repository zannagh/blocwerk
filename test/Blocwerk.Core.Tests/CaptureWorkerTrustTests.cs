using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What comes back from a compute worker is untrusted: its error text is shortened and stripped of
/// paths before an admin sees it, a bad texture set leaves no files behind, and the texture list is
/// pinned to a kiosk's own wall even when another wall's share token is presented.
/// </summary>
public class CaptureWorkerTrustTests
{
    [Fact]
    public async Task WorkerTraceback_IsStoredAsAShortReason_WithoutPaths()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        s.Client.Terminal["solve"] = new ComputeJobStatus
        {
            Status = ComputeJobStates.Failed,
            Error = "Traceback (most recent call last):\n  File \"/app/wallgeometry/solve.py\", line 12, in run\n"
                    + "    raise ValueError(msg)\nValueError: marker 12 unreadable in /tmp/jobs/7f3a/p01.jpg",
        };

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.Failed, capture.Status);
        Assert.Contains("ValueError: marker 12 unreadable", capture.Error);
        Assert.DoesNotContain("/app/", capture.Error);
        Assert.DoesNotContain("/tmp/jobs", capture.Error);
        Assert.DoesNotContain("Traceback", capture.Error);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("disk full", "disk full")]
    [InlineData("cannot open C:\\jobs\\p01.jpg now", "cannot open … now")]
    [InlineData("bad ratio 3/4 and path ~/x/y.py", "bad ratio 3/4 and path …")]
    public void ErrorText_IsSanitized(string? raw, string? expected) => Assert.Equal(expected, ComputeErrorText.Sanitize(raw));

    [Fact]
    public void ErrorText_IsCapped() => Assert.Equal(ComputeErrorText.MaxLength + 1, ComputeErrorText.Sanitize(new string('x', 5000))!.Length);

    [Fact]
    public async Task BadTextureSet_KeepsTheModel_AndLeavesNoFilesBehind()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        s.Client.Download = name => name == "facet_5a.jpg" ? "not an image"u8.ToArray() : CaptureScenario.TinyJpeg();
        var before = s.Files.ListFiles().Count;

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.SucceededWithoutTextures, (await db.WallCaptures.SingleAsync()).Status);
        Assert.True((await db.WallGeometryModels.SingleAsync()).IsActive);
        Assert.Empty(await db.WallGeometryTextures.ToListAsync());
        Assert.Equal(before, s.Files.ListFiles().Count);
    }

    [Fact]
    public async Task KioskWithAnotherWallsShareToken_GetsNoTextures()
    {
        using var h = new WallTestHarness();
        var kiosk = Substitute.For<IKioskContext>();
        using var s = new CaptureScenario(h, kiosk: kiosk);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            (await db.Walls.SingleAsync()).ShareToken = "share-a";
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        Assert.NotEmpty(await s.Service.GetActiveTexturesAsync(h.WallId, "share-a"));

        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(Guid.NewGuid());
        Assert.Empty(await s.Service.GetActiveTexturesAsync(h.WallId, "share-a"));

        kiosk.KioskWallId.Returns(h.WallId);
        Assert.NotEmpty(await s.Service.GetActiveTexturesAsync(h.WallId, "share-a"));
        Assert.NotEmpty(await s.Service.GetActiveTexturesAsync(h.WallId));
    }
}
