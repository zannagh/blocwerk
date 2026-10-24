// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Views;

/// <summary>The device's view facts (<see cref="IClientViewEnv"/>).</summary>
/// <param name="FitWidth">The photo rendition the Server pages cached for this screen (asset-plan.js's fitWidth).</param>
/// <param name="UtcOffset">Local time minus UTC.</param>
public sealed record ClientViewFacts(int FitWidth, TimeSpan UtcOffset)
{
    /// <summary>The thumbnail rendition, which the Server pages cache for every core photo too.</summary>
    public const int ThumbnailWidth = 640;

    /// <summary>Gets the facts of a browser that cannot say: a phone's rung, UTC.</summary>
    public static ClientViewFacts Default { get; } = new(1280, TimeSpan.Zero);
}
