// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Views;

/// <summary>
/// What the views need from the browser: the photo width the device cache holds for this screen and the local time
/// offset. client-view.js in the browser (<see cref="JsClientViewEnv"/>), a fixed one in the tests.
/// </summary>
public interface IClientViewEnv
{
    /// <summary>The device's view facts, read once.</summary>
    /// <returns>The facts; the defaults when the browser cannot say.</returns>
    Task<ClientViewFacts> FactsAsync();
}
