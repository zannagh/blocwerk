// <copyright file="LookOverBulkResult.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.LookOver;

/// <summary>What a bulk answer did with the findings the user confirmed.</summary>
/// <param name="Settled">How many of them were settled now.</param>
/// <param name="Asked">How many were confirmed; the rest were answered meanwhile (or a boulder uses the hold by now).</param>
public sealed record LookOverBulkResult(int Settled, int Asked);
