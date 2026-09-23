// <copyright file="CaptureVideoUploadResult.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>What capture-video.js resolves an upload to.</summary>
public sealed record CaptureVideoUploadResult(bool Ok, string? Error);
