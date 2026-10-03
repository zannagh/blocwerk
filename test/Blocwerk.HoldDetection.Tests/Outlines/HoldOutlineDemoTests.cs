using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// Writes before/after overlays of every real fixture when <c>BLOCWERK_OUTLINE_DEMO_DIR</c> is set
/// (a visual check, not an assertion); skipped otherwise.
/// </summary>
public class HoldOutlineDemoTests
{
    [SkippableFact]
    public void WriteOverlays()
    {
        string? dir = Environment.GetEnvironmentVariable("BLOCWERK_OUTLINE_DEMO_DIR");
        Skip.If(string.IsNullOrEmpty(dir), "Set BLOCWERK_OUTLINE_DEMO_DIR to write overlays.");
        Directory.CreateDirectory(dir!);
        var lines = new List<string>();
        for (int i = 0; i < HoldFixtures.All.Length; i++)
        {
            HoldFixture f = HoldFixtures.All[i];
            using var img = HoldFixtures.Load(f.File);
            var (result, seed, _, _) = HoldFixtures.Outline(f);
            OutlineDemoRenderer.Write(img, seed, result, Path.Combine(dir!, $"{i}_{f.File}.jpg"));
            HoldFingerprint fp = result.Fingerprint;
            lines.Add($"{i} {f.File} {result.Method} conf={result.Confidence:F2} pts={result.Polygon.Count} area={result.AreaPx:F0} " +
                $"Lab=({fp.L:F0},{fp.A:F0},{fp.B:F0}) asp={fp.Aspect:F2} sol={fp.Solidity:F2} hu=[{string.Join(",", fp.Hu.Take(4).Select(h => h.ToString("F2")))}]");
        }

        File.WriteAllLines(Path.Combine(dir!, "summary.txt"), lines);
    }
}
