// <copyright file="EditSignal.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>What a circuit is telling the registry about one of its editing leases (see <see cref="EditActivitySignals"/>).</summary>
public enum EditSignal
{
    /// <summary>The connection is up. Proves liveness only, never presence.</summary>
    Heartbeat,

    /// <summary>Real user input in the editor: the only signal that proves a person is there.</summary>
    Input,

    /// <summary>The editor's page was hidden, frozen or unloaded.</summary>
    Hidden,

    /// <summary>The editor's page is visible again. Not input: it clears the hidden mark and nothing else.</summary>
    Visible,
}
