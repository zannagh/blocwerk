// <copyright file="RefreshNumber.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>One headline number of the check step.</summary>
/// <param name="Value">The number.</param>
/// <param name="Label">What it counts.</param>
/// <param name="Attention">It deserves a look (drawn in the attention colour).</param>
internal sealed record RefreshNumber(int Value, string Label, bool Attention = false);
