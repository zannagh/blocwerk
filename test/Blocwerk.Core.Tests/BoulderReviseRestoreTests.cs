// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A revise kept on the device across a deploy comes back whole, the hold type included: the snapshot writes it (so a
/// change of the hold type alone counts as unsaved), and RestoreKeptAsync reads every field back. Checked against the
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
    public void TheSnapshot_CarriesTheHoldType_AndTheSaveBeforeADeploySendsIt()
    {
        var page = Source("BoulderRevise.razor");
        var snapshot = page[page.IndexOf("private string SnapshotJson", StringComparison.Ordinal)..];
        snapshot = snapshot[..snapshot.IndexOf("});", StringComparison.Ordinal)];
        Assert.Contains("holdType = BoulderHoldTypeChange.ToForm(_holdType)", snapshot, StringComparison.Ordinal);

        var flush = Source("BoulderRevise.DeployFlush.cs");
        var save = flush[flush.IndexOf("private async Task<bool> TrySaveDraftAsync", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("new BoulderHoldTypeChange(_holdType)", save, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeptSnapshot_ReadsBackItsHoldType()
    {
        var kept = BoulderFormSnapshot.TryParse("""{"id":"11111111-1111-1111-1111-111111111111","holdType":"Sloper"}""");
        Assert.Equal(new BoulderHoldTypeChange(HoldHandType.Sloper), BoulderHoldTypeChange.FromForm(kept!.HoldType));
    }

    [Theory]
    [InlineData(null, false, null)]
    [InlineData("", true, null)]
    [InlineData("crimp", true, HoldHandType.Crimp)]
    [InlineData("bogus", false, null)]
    public void TheFormValue_LeavesClearsOrSets(string? value, bool changes, HoldHandType? type)
    {
        var change = BoulderHoldTypeChange.FromForm(value);
        Assert.Equal(changes, change.HasValue);
        Assert.Equal(type, change?.Type);
    }
}
