// <copyright file="RefreshVideoRules.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>What the browser checks about each video before it sends one (refresh-upload.js).</summary>
/// <param name="Accepted">A video is used here at all.</param>
/// <param name="Room">How many more videos the update takes.</param>
/// <param name="MaxBytes">The largest video the server stores.</param>
/// <param name="Refusal">What a video that is not used is told.</param>
public sealed record RefreshVideoRules(bool Accepted, int Room, long MaxBytes, string Refusal);
