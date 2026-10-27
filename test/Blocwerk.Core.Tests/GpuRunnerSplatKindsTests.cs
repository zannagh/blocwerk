// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Splat prepare / finish jobs in the runner queue: only runners that name <c>splat-prepare</c> / <c>splat-finish</c> and have the
/// memory are offered one; a runner without capabilities, or with every older list, never is.
/// </summary>
public class GpuRunnerSplatKindsTests
{
    [Fact]
    public void Capabilities_KnowTheNewNames_AndNoOlderListAllowsThem()
    {
        Assert.Equal("splat,splat-prepare,splat-finish", RunnerCapabilities.Store(["Splat", "splat-prepare", "splat-finish", "bogus"]));
        Assert.Equal("splat-prepare", RunnerCapabilities.KindName(GpuJobKind.SplatPrepare));
        Assert.Equal("splat-finish", RunnerCapabilities.KindName(GpuJobKind.SplatFinish));
        foreach (var kind in new[] { GpuJobKind.SplatPrepare, GpuJobKind.SplatFinish })
        {
            Assert.False(RunnerCapabilities.Allows(null, kind));
            Assert.False(RunnerCapabilities.Allows("splat", kind));
            Assert.False(RunnerCapabilities.Allows("splat,textures,detect-holds,register-panels", kind));
        }

        Assert.True(RunnerCapabilities.Allows("splat-prepare", GpuJobKind.SplatPrepare));
        Assert.False(RunnerCapabilities.Allows("splat-prepare", GpuJobKind.SplatFinish));
        Assert.True(RunnerCapabilities.Allows("splat-finish", GpuJobKind.SplatFinish));
        Assert.False(RunnerCapabilities.Allows("splat-finish,splat-prepare", GpuJobKind.Splat));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("splat")]
    [InlineData("splat,textures,detect-holds,register-panels")]
    public async Task ARunnerWithoutTheCapabilities_NeverClaimsThem_HoweverMuchMemoryItHas(string? capabilities)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("old", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, 512_000, capabilities?.Split(',') ?? []);
        var prepare = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);
        var finish = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatFinish);

        Assert.Null(await f.Queue.TryClaimAsync(stored, null, CancellationToken.None));

        // The capability-less runner stays what it was: a splat trainer.
        var splat = await f.AddJobAsync(h.WallId);
        Assert.Equal(splat.Id, (await f.Queue.TryClaimAsync(stored, null, CancellationToken.None))?.Id);
        foreach (var id in new[] { prepare.Id, finish.Id })
        {
            Assert.Equal(GpuJobStatus.Queued, (await SplatJobSupport.JobAsync(h, id)).Status);
        }
    }

    [Fact]
    public async Task ARunnerWithOnlyThatCapability_ClaimsOnlyThatKind_AndSeesNoSplats()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("cpu", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, 16_000, RunnerCapabilities.SplatFinish);
        await f.AddJobAsync(h.WallId);
        await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);
        var finish = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatFinish);

        var claimed = await f.Queue.TryClaimAsync(stored, null, CancellationToken.None);

        Assert.Equal((finish.Id, GpuJobKind.SplatFinish), (claimed?.Id, claimed?.Kind));
    }

    [Theory]
    [InlineData(GpuJobKind.SplatPrepare, 6143, false)]
    [InlineData(GpuJobKind.SplatPrepare, 6144, true)]
    [InlineData(GpuJobKind.SplatFinish, 3071, false)]
    [InlineData(GpuJobKind.SplatFinish, 3072, true)]
    public async Task TheMemoryBudget_DecidesAgainstTheKindsDefault(GpuJobKind kind, int budgetMb, bool offered)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("pc", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, budgetMb, SplatJobSupport.PrepareAndFinish);
        var job = await SplatJobSupport.AddJobAsync(f, h.WallId, kind);

        Assert.Equal(kind == GpuJobKind.SplatPrepare ? 6144 : 3072, job.RequiredMemoryMb);
        Assert.Equal(offered ? job.Id : null, (await f.Queue.TryClaimAsync(stored, null, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task ARunnerThatReportsNoBudget_IsNeverOfferedThem()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("pc", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, 0, SplatJobSupport.PrepareAndFinish);
        await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatFinish);

        Assert.Null(stored.MemoryBudgetMb);
        Assert.Null(await f.Queue.TryClaimAsync(stored, null, CancellationToken.None));
    }

    [Fact]
    public async Task TheDefaultsAreConfigurable_AndAJobsOwnRequirementWins()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(
            h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, PrepareMemoryMb = 2000, FinishMemoryMb = 1000 });
        var (runner, _) = await f.AddRunnerAsync("pc", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, 3000, SplatJobSupport.PrepareAndFinish);
        var big = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare, requiredMb: 9000);
        var small = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatFinish);

        Assert.Equal(1000, small.RequiredMemoryMb);
        Assert.Equal(9000, big.RequiredMemoryMb);
        Assert.Equal(small.Id, (await f.Queue.TryClaimAsync(stored, null, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task TheClaimAnswer_NamesTheKindAndMemory_AndOffersNoPreviews_AtDraftQuality()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("pc", walls: h.WallId, maxQuality: "draft");
        var stored = await SplatJobSupport.HelloAsync(f, runner, 16_000, SplatJobSupport.PrepareAndFinish);
        var job = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);

        var claim = await f.Queue.ClaimAsync(stored, TimeSpan.Zero, null, CancellationToken.None);

        Assert.Equal((job.Id, "splat-prepare", false, "draft", 6144), (claim?.JobId, claim?.Kind, claim?.Previews, claim?.Quality, claim?.RequiredMemoryMb));
        var (outcome, path) = await f.Queue.BundleForRunnerAsync(stored, job.Id, CancellationToken.None);
        Assert.Equal(RunnerJobOutcome.Ok, outcome);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path!);
        Assert.Equal(new[] { "p01.jpg", "parts.json" }, zip.Entries.Select(e => e.FullName).Order().ToArray());
    }

    [Fact]
    public async Task ASharedRunner_GetsThemOnlyForWallsThatApprovedIt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var (shared, _) = await f.AddRunnerAsync("shared", shared: true, walls: other);
        var stored = await SplatJobSupport.HelloAsync(f, shared, 16_000, SplatJobSupport.PrepareAndFinish);
        var job = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);

        Assert.Null(await f.Queue.TryClaimAsync(stored, null, CancellationToken.None));

        await f.ApproveAsync(h.WallId, shared);
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(stored, null, CancellationToken.None))?.Id);
    }
}
