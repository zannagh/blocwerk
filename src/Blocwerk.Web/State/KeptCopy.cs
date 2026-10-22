// <copyright file="KeptCopy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// An editor's state taken back from the browser: its payload, and why it was kept ("deploy" by a pre-deploy flush,
/// "hidden" copied when the page was hidden).
/// </summary>
public sealed record KeptCopy(string Payload, string Reason)
{
    /// <summary>Kept by a pre-deploy flush (rather than copied while the page was hidden).</summary>
    public bool FromDeploy => Reason == "deploy";
}
