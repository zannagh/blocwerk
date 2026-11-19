// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using Blocwerk.Web.State;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A create kept on the device across a deploy comes back whole: BoulderCreate's ApplySnapshot reads every field the
/// snapshot carries (hands-follow-feet was once left behind, so a variant of a boulder with dedicated footholds came back
/// with them on). Checked against the source, since the page has no harness of its own.
/// </summary>
public class BoulderCreateRestoreTests
{
    [Fact]
    public void ApplySnapshot_RestoresEveryFieldOfTheSnapshot()
    {
        var source = File.ReadAllText(Path.Combine(
            FeedBulkSiteScanner.RepoRoot(), "src", "Blocwerk.Web", "Components", "Pages", "Walls", "BoulderCreate.DeployFlush.cs"));
        var body = source[source.IndexOf("private void ApplySnapshot", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }\n", StringComparison.Ordinal)];

        var missing = typeof(BoulderFormSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !body.Contains($"snapshot.{name}", StringComparison.Ordinal));

        Assert.Empty(missing);
    }
}
