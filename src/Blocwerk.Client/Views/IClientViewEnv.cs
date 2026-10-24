// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Views;

/// <summary>
/// What the views need from the browser: the photo width the device cache holds for this screen and the local time
/// offsets. client-view.js in the browser (<see cref="JsClientViewEnv"/>), a fixed one in the tests.
/// </summary>
public interface IClientViewEnv
{
    /// <summary>The device's view facts, read once.</summary>
    /// <returns>The facts; the defaults when the browser cannot say.</returns>
    Task<ClientViewFacts> FactsAsync();

    /// <summary>
    /// The local time offset at each of <paramref name="moments"/> (the client has no time zone data, and a summer
    /// date has another offset than today's).
    /// </summary>
    /// <param name="moments">The moments a view shows as dates.</param>
    /// <returns>One offset per moment, in order; today's for every moment when the browser cannot say.</returns>
    Task<IReadOnlyList<TimeSpan>> OffsetsAsync(IReadOnlyList<DateTimeOffset> moments);
}
