// <copyright file="HoldPlacementUnmeasured.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>The holds a placement run deliberately left unmeasured, in words for the run summary.</summary>
public static class HoldPlacementUnmeasured
{
    /// <summary>
    /// E.g. "12 holds left unmeasured because the two photos disagree, 3 because their photo's matches do not reach them.";
    /// empty when the run left none unmeasured on purpose.
    /// </summary>
    /// <param name="panels">The run's panel summaries.</param>
    /// <returns>The sentence, or an empty string.</returns>
    public static string Text(IEnumerable<HoldPlacementPanelSummary> panels)
    {
        var list = panels.ToList();
        var (disagreed, unsupported) = (list.Sum(p => p.Disagreed), list.Sum(p => p.Unsupported));
        const string Disagree = "because the two photos disagree";
        const string Unreached = "because their photo's matches do not reach them";
        return (disagreed, unsupported) switch
        {
            (> 0, > 0) => $"{Holds(disagreed)} left unmeasured {Disagree}, {unsupported} {Unreached}.",
            (> 0, _) => $"{Holds(disagreed)} left unmeasured {Disagree}.",
            (_, > 0) => $"{Holds(unsupported)} left unmeasured {Unreached}.",
            _ => string.Empty,
        };
    }

    private static string Holds(int n) => n == 1 ? "1 hold" : $"{n} holds";
}
