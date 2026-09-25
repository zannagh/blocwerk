// <copyright file="HoldProposalAcceptRequest.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Controllers;

/// <summary>Body of an accept: the new hold's colour key and category (both optional).</summary>
/// <param name="Color">Colour key (<c>HoldPalette</c>), or null.</param>
/// <param name="Category">"Hand" (default) or "Foot".</param>
public sealed record HoldProposalAcceptRequest(string? Color, string? Category);
