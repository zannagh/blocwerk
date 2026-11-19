// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A revise kept on the device across a deploy comes back whole, the hold types included: the snapshot writes them (so a
/// change of the hold types alone counts as unsaved), and RestoreKeptAsync reads every field back. Checked against the
/// source, since the page has no harness of its own.
/// </summary>
public class BoulderReviseRestoreTests
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(
        FeedBulkSiteScanner.RepoRoot(), "src", "Blocwerk.Web", "Components", "Pages", "Walls", file));

    [Fact]
    public void RestoreKept_RestoresEveryFieldOfTheSnapshot()
    {
        var source = Source("BoulderRevise.DeployFlush.cs");
        var body = source[source.IndexOf("private async Task RestoreKeptAsync", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }\n", StringComparison.Ordinal)];

        var missing = typeof(BoulderFormSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !body.Contains($"snapshot.{name}", StringComparison.Ordinal));

        Assert.Empty(missing);
    }

    [Fact]
    public void TheSnapshot_CarriesTheHoldTypes_AndTheSaveBeforeADeploySendsThem()
    {
        var page = Source("BoulderRevise.razor");
        var snapshot = page[page.IndexOf("private string SnapshotJson", StringComparison.Ordinal)..];
        snapshot = snapshot[..snapshot.IndexOf("});", StringComparison.Ordinal)];
        Assert.Contains("holdTypes = BoulderHoldTypeChange.ToNames(_holdTypes)", snapshot, StringComparison.Ordinal);

        var flush = Source("BoulderRevise.DeployFlush.cs");
        var save = flush[flush.IndexOf("private async Task<bool> TrySaveDraftAsync", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("new BoulderHoldTypeChange(_holdTypes)", save, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeptSnapshot_ReadsBackItsHoldTypes()
    {
        var kept = BoulderFormSnapshot.TryParse("""{"id":"11111111-1111-1111-1111-111111111111","holdTypes":["Sloper","Heel hook"]}""");
        Assert.Equal(BoulderHoldType.Sloper | BoulderHoldType.HeelHook, BoulderHoldTypeChange.FromRequest(kept!.HoldTypes, kept.HoldType)!.Value.ApplyTo(BoulderHoldType.Jug));
    }

    [Fact]
    public void ASnapshotKeptBy_A070Page_SetsItsGrip_AndKeepsTheHooks()
    {
        var kept = BoulderFormSnapshot.TryParse("""{"id":"11111111-1111-1111-1111-111111111111","holdType":"Sloper"}""");
        Assert.Equal(
            BoulderHoldType.Sloper | BoulderHoldType.ToeHook,
            BoulderHoldTypeChange.FromRequest(kept!.HoldTypes, kept.HoldType)!.Value.ApplyTo(BoulderHoldType.Crimp | BoulderHoldType.ToeHook));
    }
}
