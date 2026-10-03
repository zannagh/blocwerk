using System.Diagnostics;
using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;
using Blocwerk.HoldDetection.Matching;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// OFFLINE evaluation (not a regression test): runs the real matcher on pairs of the capture-1 glyph photos
/// with and without the marker seed, scoring against ground-truth identities projected from the solved wall
/// model. Skipped unless <c>BLOCWERK_SEED_EVAL</c> names the scratch directory holding
/// <c>seedeval/glyph_fixture.json</c>, <c>glyphs/png/</c> and <c>attic-metric/wall-geometry.snapshot.json</c>.
/// </summary>
public class SeedEvaluationTests
{
    /// <summary>The matcher's documented auto-accept tier; wrong proposals above it are the confidently-wrong ones.</summary>
    private const double ConfidentAt = 0.45;

    private static readonly (string Left, string Right)[] Pairs =
    [
        ("IMG_2770", "IMG_2783"),
        ("IMG_2770", "IMG_2775"),
        ("IMG_2770", "IMG_2771"),
        ("IMG_2771", "IMG_2775"),
        ("IMG_2770", "IMG_2782"),
        ("IMG_2774", "IMG_2782"),
        ("IMG_2771", "IMG_2783"),
    ];

    private readonly ITestOutputHelper output;

    public SeedEvaluationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [SkippableFact]
    public void GlyphPairs_WithAndWithoutSeed()
    {
        var root = Environment.GetEnvironmentVariable("BLOCWERK_SEED_EVAL");
        Skip.If(string.IsNullOrEmpty(root) || !Directory.Exists(root), "offline evaluation: set BLOCWERK_SEED_EVAL");

        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root!, "seedeval", "glyph_fixture.json")))!;
        var document = WallGeometryDocument.Parse(File.ReadAllText(Path.Combine(root!, "attic-metric", "wall-geometry.snapshot.json")));
        var pngDir = Path.Combine(root!, "glyphs", "png");
        var only = Environment.GetEnvironmentVariable("BLOCWERK_SEED_EVAL_PAIRS");
        foreach (var (l, r) in Pairs.Where(p => only is null || only.Contains(p.Left + "-" + p.Right, StringComparison.Ordinal)))
        {
            var left = SeedEvalScenario.Load(fixture, pngDir, l);
            var right = SeedEvalScenario.Load(fixture, pngDir, r);
            RunPair(left, right, document);
        }
    }

    private void RunPair(SeedEvalPhoto left, SeedEvalPhoto right, WallGeometryDocument document)
    {
        var shared = OverlapSeedBuilder.Build(left.SeedPhoto(), right.SeedPhoto(), null, left.SeedHolds());
        var model = OverlapSeedBuilder.Build(left.SeedPhoto(), right.SeedPhoto(), document, left.SeedHolds());
        var anchors = WallSpaceAnchorProposer.Propose(left.WallSpace(), right.WallSpace());
        int anchorsCorrect = anchors.Count(a => left.Holds[a.LeftHoldId].Id == right.Holds[a.RightHoldId].Id);
        output.WriteLine(
            $"== {left.Name} -> {right.Name}: holds {left.Holds.Count}/{right.Holds.Count}, markers {left.Markers.Count}/{right.Markers.Count}, "
            + $"shared-seed {Describe(shared)}, model-seed {Describe(model)}, anchors {anchors.Count} ({anchorsCorrect} correct)");

        Run("no seed", left, right, null);
        Run("shared markers", left, right, shared);
        Run("model facet", left, right, model);
        Run("model + anchors", left, right, (model ?? new HoldOverlapSeed()) with { Anchors = anchors });
    }

    private void Run(string label, SeedEvalPhoto left, SeedEvalPhoto right, HoldOverlapSeed? seed)
    {
        if (label != "no seed" && seed is null)
        {
            output.WriteLine($"  {label,-16} n/a");
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = new OpenCvHoldOverlapMatcher().Match(
                left.Image, left.MatcherHolds(), right.Image, right.MatcherHolds(), HoldOverlapDirection.Right, null, seed);
            var (correct, wrong, missed, truth) = SeedEvalScenario.Score(left, right, result);
            var confident = result with { Proposals = result.Proposals.Where(p => p.Confidence >= ConfidentAt).ToList() };
            var (correctHi, wrongHi, _, _) = SeedEvalScenario.Score(left, right, confident);
            int wallspace = result.Proposals.Count(p => p.Rescue == AnchorReconciler.RescueTag);
            output.WriteLine(
                $"  {label,-16} correct {correct,4}  wrong {wrong,4}  missed {missed,4} / {truth}  "
                + $"(conf>={ConfidentAt}: correct {correctHi,4} wrong {wrongHi,4}; wallspace-added {wallspace}, {sw.Elapsed.TotalSeconds:F0}s)");
        }
        catch (Exception ex)
        {
            output.WriteLine($"  {label,-16} FAILED: {ex.Message}");
        }
    }

    private static string Describe(HoldOverlapSeed? s) =>
        s is null ? "none" : $"{s.Source}/{s.FacetId ?? "-"}/{s.MarkerCount}m/rms {s.FitRmsPx:F1}px/priors {s.PriorPairs.Count}";
}
