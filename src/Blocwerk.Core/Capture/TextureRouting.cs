// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>Where a texture re-render of a quality runs.</summary>
public enum TextureRoute
{
    /// <summary>On the host's geometry worker (the quality fits its memory budget).</summary>
    Host = 0,

    /// <summary>On an online 3D runner with more memory (the quality would not fit the host in full).</summary>
    Runner = 1,
}

/// <summary>A 3D runner that could render a quality in full, and how busy the textures queue is.</summary>
public sealed record TextureRunnerOffer(string Name, bool Paused, bool Busy, int Waiting, int Running);

/// <summary>The route chosen for one quality, with the plain-language line the picker shows under it.</summary>
public sealed record TextureQualityRoute(TextureQuality Quality, TextureRoute Route, TextureRunnerOffer? Runner, string? Note);

/// <summary>
/// What the "Render textures again" picker shows: the host's estimate per quality (<see cref="TextureQualityEstimate"/>),
/// the route each quality takes, and the best quality that fits the host in full (what to offer when no runner is online).
/// <see cref="RunnerOnline"/>: some 3D runner that renders textures is online (paused or not), whatever the memory.
/// </summary>
public sealed record TextureRouting(
    IReadOnlyList<TextureQualityEstimate> Estimates,
    IReadOnlyList<TextureQualityRoute> Routes,
    TextureQuality BestHostQuality,
    bool RunnerOnline)
{
    /// <summary>No routing known (no model): everything runs on the host.</summary>
    public static TextureRouting None { get; } = new([], [], TextureQuality.Standard, false);

    /// <summary>The route of <paramref name="quality"/> (the host when none is recorded).</summary>
    public TextureQualityRoute RouteOf(TextureQuality quality) =>
        Routes.FirstOrDefault(r => r.Quality == quality) ?? new TextureQualityRoute(quality, TextureRoute.Host, null, null);
}

/// <summary>The routing decision: pure, so it is tested without a queue.</summary>
public static class TextureRoutes
{
    /// <summary>
    /// A quality that fits the host in full runs there. One that would blend fewer views (or fall back to one photo) goes to a
    /// runner that can render it in full, when there is one; else it stays on the host and says why it is reduced.
    /// </summary>
    /// <param name="host">The host's estimates, per quality.</param>
    /// <param name="runnerFor">The runner able to render a quality in full (online, textures capable, enough memory), or null.</param>
    /// <param name="runnerOnline">Whether any textures-capable runner is online, whatever its memory.</param>
    public static TextureRouting Decide(
        IReadOnlyList<TextureQualityEstimate> host, Func<TextureQuality, TextureRunnerOffer?> runnerFor, bool runnerOnline)
    {
        var routes = host.Select(e => Route(e, e.Fit == TextureBlendFit.Full ? null : runnerFor(e.Quality), runnerOnline)).ToList();
        var best = host.Where(e => e.Fit == TextureBlendFit.Full).Select(e => e.Quality).DefaultIfEmpty(TextureQuality.Standard).Max();
        return new TextureRouting(host, routes, best, runnerOnline);
    }

    private static TextureQualityRoute Route(TextureQualityEstimate e, TextureRunnerOffer? runner, bool runnerOnline)
    {
        if (e.Fit == TextureBlendFit.Full)
        {
            return new TextureQualityRoute(e.Quality, TextureRoute.Host, null, null);
        }

        if (runner is not null)
        {
            return new TextureQualityRoute(e.Quality, TextureRoute.Runner, runner, null);
        }

        var note = runnerOnline
            ? "No online 3D runner has enough memory for this either."
            : "No 3D runner that renders textures is online.";
        return new TextureQualityRoute(e.Quality, TextureRoute.Host, null, note);
    }
}

/// <summary>What the admin picked in the "Render textures again" picker.</summary>
public sealed record TextureRerenderChoice(TextureQuality Quality, TextureRoute Route);
