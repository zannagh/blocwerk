// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The mobile-data notice while it waits for the viewer (<see cref="Wall3DStage"/>).</summary>
/// <param name="Bytes">The download size to show, or null.</param>
/// <param name="Answer">Completed with true to start the 3D view, false to leave it unloaded.</param>
public sealed record Wall3DNoticeState(long? Bytes, TaskCompletionSource<bool> Answer);
