// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The wall's update mode as the settings card shows it.</summary>
/// <param name="Mode">The stored mode.</param>
/// <param name="CanSwitchToPanels">False once a scan has been applied to the wall.</param>
public sealed record WallUpdateModeView(WallUpdateMode Mode, bool CanSwitchToPanels);
