// <copyright file="CaptureNotActivatedException.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>
/// Ends a capture whose model was stored but not activated: not an error, the capture ends as
/// <see cref="Entities.WallCaptureStatus.StoredNotActivated"/> with the message (the reason) shown to the admin.
/// </summary>
public sealed class CaptureNotActivatedException(string message) : Exception(message);
