// <copyright file="BigWallUpdateSourceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Source assertions for the step-by-step wizard (no component-test host here): a failed neighbour save keeps the panel
/// (C6), the confirm step counts what the session holds (C1), the touch-up's delete asks before taking a matched hold,
/// and every write names the session the wizard looks at (C2).
/// </summary>
public class BigWallUpdateSourceTests
{
    private const string Shared = "src/Blocwerk.Web/Components/Shared/";

    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void AFailedNeighbourSave_StaysOnThePanel()
    {
        var code = Read(Shared + "BigWallUpdate.Neighbours.cs");

        var confirm = Body(code, "private async Task OnNeighbourConfirm(");
        var save = confirm.IndexOf("if (!await SaveNeighbourAsync(set))", StringComparison.Ordinal);
        var bail = confirm.IndexOf("return;", save, StringComparison.Ordinal);
        var advance = confirm.IndexOf("await AdvanceNeighbourAsync();", StringComparison.Ordinal);
        Assert.True(save > 0 && bail > save && advance > bail, "OnNeighbourConfirm must return before advancing when the save failed");
        Assert.Contains("private async Task<bool> SaveNeighbourAsync(", code);
        Assert.Contains("FinishFailed()", confirm);
    }

    [Fact]
    public void TheConfirmCounts_ReadTheSession_NotTheCircuitsMemory()
    {
        var summary = Read(Shared + "BigWallUpdate.ConfirmSummary.cs");
        var promote = Read(Shared + "BigWallUpdate.Promote.cs");

        Assert.Contains("await Sessions.GetDecisionsAsync(WallId)", summary);
        Assert.Contains("private int ChangedCount => SummaryCarryover.Count(", summary);
        Assert.DoesNotContain("ChangedCount =>", promote);
        foreach (var file in new[] { "BigWallUpdate.Shapes.cs", "BigWallUpdate.Session.cs" })
        {
            Assert.DoesNotContain("await LoadShapeSummaryAsync();", Read(Shared + file));
        }
    }

    [Fact]
    public void TheTouchupDelete_RefusesAMatchedHold_AndAsks()
    {
        var code = Read(Shared + "TouchupStep.razor.cs");
        var guard = Read(Shared + "TouchupStep.Guard.cs");
        var markup = Read(Shared + "TouchupStep.razor");

        Assert.Contains("DeleteGuardedAsync(id, StagedTwinDeletion.Refuse)", code);
        Assert.Contains("catch (StagedHoldIsTwinException ex)", guard);
        Assert.Contains("@onclick=\"ConfirmTwinDeleteAsync\"", markup);
        Assert.DoesNotContain("DeleteStagedHoldAsync(", code);
    }

    [Fact]
    public void EveryWizardWrite_NamesTheSession()
    {
        var plainCalls = new Regex(@"(Sessions|WallPanelService)\.(SetPhaseAsync|Save\w+Async|Clear\w+Async|Decide\w+Async|(Add|Update|Delete)StagedHoldAsync)\(");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoRoot, Shared), "*.cs"))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("BigWallUpdate", StringComparison.Ordinal) && !name.StartsWith("CarryoverReview", StringComparison.Ordinal)
                && !name.StartsWith("TouchupStep", StringComparison.Ordinal) && !name.StartsWith("PanelOverlapStepper", StringComparison.Ordinal))
            {
                continue;
            }

            var code = File.ReadAllText(path);
            foreach (Match call in plainCalls.Matches(code))
            {
                // The whole call, up to the end of its statement.
                var statement = code[call.Index..code.IndexOf(';', call.Index)];
                Assert.True(
                    statement.Contains("SessionId", StringComparison.Ordinal) || statement.Contains("_sessionInfo?.Id", StringComparison.Ordinal),
                    $"{name}: {statement.Trim()} does not name the session");
            }
        }
    }

    /// <summary>The text of one method, from its signature to the next member at the same indentation.</summary>
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} missing");
        var end = code.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return code[start..end];
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
