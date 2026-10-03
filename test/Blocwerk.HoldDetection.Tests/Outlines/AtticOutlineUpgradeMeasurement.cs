using System.Diagnostics;
using System.Text.Json;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Services;
using Blocwerk.HoldDetection.Outlines;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// Read-only dry run of the outline upgrade against an exported wall (photos + <c>holds.json</c>, never
/// committed): <c>BLOCWERK_ATTIC_DIR</c> points at the export, <c>BLOCWERK_ATTIC_GENERATION</c> picks the
/// live generation (default 3), and <c>BLOCWERK_ATTIC_OUT</c> optionally receives overlays. Skipped otherwise.
/// </summary>
public class AtticOutlineUpgradeMeasurement(ITestOutputHelper output)
{
    [SkippableFact]
    public void DryRun_OnTheExportedLiveWall()
    {
        string? dir = Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_DIR");
        Skip.If(string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir!, "holds.json")), "Set BLOCWERK_ATTIC_DIR to an export.");
        int generation = int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_GENERATION"), out var g) ? g : 3;
        string? outDir = Environment.GetEnvironmentVariable("BLOCWERK_ATTIC_OUT");

        var rows = JsonSerializer.Deserialize<List<AtticExportedHold>>(File.ReadAllText(Path.Combine(dir!, "holds.json")))!;
        var live = rows.Where(r => r.Generation == generation && r.WallPanelId is not null).ToList();
        var auto = new List<HoldOutlineUpgradeProposal>();
        var all = new List<HoldOutlineUpgradeProposal>();
        int photos = 0;
        foreach (var panel in live.GroupBy(r => (PanelCol: r.PanelCol ?? 0, PanelRow: r.PanelRow ?? 0)).OrderBy(p => p.Key))
        {
            var path = Path.Combine(dir!, "photos", $"panel_c{panel.Key.PanelCol}_r{panel.Key.PanelRow}_g{generation}.jpg");
            var holds = panel.Select(r => r.ToHold()).ToList();
            photos++;
            var watch = Stopwatch.StartNew();
            using var session = new OpenCvHoldOutlineService().OpenSession(File.ReadAllBytes(path));
            var withManual = HoldOutlineUpgradePlanner.Plan(session, holds, includeManual: true);
            output.WriteLine($"panel c{panel.Key.PanelCol}: {holds.Count} live holds, {withManual.Count} circles outlined in {watch.ElapsedMilliseconds} ms");
            all.AddRange(withManual);
            auto.AddRange(withManual.Where(p => p.Hold.IsAutoDetected));
            if (!string.IsNullOrEmpty(outDir))
            {
                AtticOverlayRenderer.Write(path, withManual, Path.Combine(outDir, $"attic_c{panel.Key.PanelCol}_upgrade.jpg"));
            }
        }

        Report("auto only (default)", photos, live.Count, auto);
        Report("including manual", photos, live.Count, all);
    }

    private void Report(string label, int photos, int liveCount, List<HoldOutlineUpgradeProposal> proposals)
    {
        var p = HoldOutlineUpgradeService.Summarize(photos, proposals);
        output.WriteLine(
            $"{label}: live {liveCount}, eligible {p.Eligible}, would outline {p.WouldOutline} (contour {p.ByContour}, grabcut {p.ByGrabCut}), "
            + $"keep circle {p.WouldKeepCircle} (leak-rejected {p.RejectedAsLeak}), with holes {p.WithHoles}, fingerprints {p.WouldFingerprint}");
        output.WriteLine($"  examples: {string.Join(", ", p.ExampleHoldIds)}");
    }
}
