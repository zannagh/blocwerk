// <copyright file="WallBigUpdateService.LookLog.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// What the look check measured, for reading the score distributions off a running instance: one line per panel with
/// the counts and a histogram, and at Debug one line per pair and per predicted spot. Nothing depends on these lines.
/// </summary>
public partial class WallBigUpdateService
{
    private void LogLooks(LookOutcome looks)
    {
        foreach (var panel in looks.Pairs.Select(p => p.Panel).Concat(looks.Spots.Select(s => s.Panel)).Distinct())
        {
            var pairs = looks.Pairs.Where(p => p.Panel == panel).ToList();
            var spots = looks.Spots.Where(s => s.Panel == panel).ToList();
            logger.LogInformation(
                "[Looks] panel={Col},{Row} pairs={Pairs} same={Same} unsure={Unsure} replaced={Replaced} mispaired={Mispaired} unmeasured={Unmeasured} hist={Hist:l} | "
                + "spots={Spots} gone={Gone} kept={Kept} unmeasured={SpotsUnmeasured} hist={SpotHist:l}",
                panel.Col, panel.Row, pairs.Count,
                pairs.Count(p => p.Look == HoldLook.Same), pairs.Count(p => p.Look == HoldLook.Unsure),
                pairs.Count(p => p.Look == HoldLook.Replaced && !p.Mispaired), pairs.Count(p => p.Look == HoldLook.Replaced && p.Mispaired),
                pairs.Count(p => p.Look is null), HoldLookCheck.Histogram(pairs.Select(p => p.Score).OfType<double>()),
                spots.Count, spots.Count(s => SpotVerdict(s) == "gone"), spots.Count(s => SpotVerdict(s) == "kept"),
                spots.Count(s => s.Score is null), HoldLookCheck.Histogram(spots.Select(s => s.Score).OfType<double>()));
        }

        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        foreach (var p in looks.Pairs)
        {
            logger.LogDebug(
                "[Looks] pair old={OldHoldId} twin={TwinId} panel={Col},{Row} score={Score:l} verdict={Verdict:l} oldRadiusPx={OldRadiusPx:l} twinRadiusPx={TwinRadiusPx:l}",
                p.Pair.OldHoldId, p.Pair.NewHoldId, p.Panel.Col, p.Panel.Row, Number(p.Score, "0.000"), PairVerdict(p), Number(p.OldRadiusPx, "0"), Number(p.TwinRadiusPx, "0"));
        }

        foreach (var s in looks.Spots)
        {
            logger.LogDebug(
                "[Looks] spot old={OldHoldId} twin=- panel={Col},{Row} score={Score:l} verdict={Verdict:l} oldRadiusPx={OldRadiusPx:l} twinRadiusPx=-",
                s.OldHoldId, s.Panel.Col, s.Panel.Row, Number(s.Score, "0.000"), SpotVerdict(s), Number(s.OldRadiusPx, "0"));
        }
    }

    private static string PairVerdict(JudgedPair pair) => pair.Look switch
    {
        null => "unmeasured",
        HoldLook.Replaced when pair.Mispaired => "mispaired",
        var look => look.Value.ToString().ToLowerInvariant(),
    };

    private string SpotVerdict(JudgedSpot spot) =>
        spot.Score is not { } score ? "unmeasured" : HoldLookCheck.LooksGone(score, moveOptions) ? "gone" : "kept";

    private static string Number(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "-";
}
