// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Scan;

namespace Blocwerk.Web.Controllers;

/// <summary>The wall's "iPhone scan" setting as the web settings card shows it, for the companion app.</summary>
/// <param name="Mode">Panels or Scan.</param>
/// <param name="CanSwitchToPanels">False once a scan has been applied to the wall.</param>
/// <param name="Scans">The wall's ten most recent scans, newest first.</param>
/// <param name="MayDeleteScans">True when the caller may remove a received scan (an administrator of the installation).</param>
public sealed record DeviceScanModeResponse(string Mode, bool CanSwitchToPanels, IReadOnlyList<WallScanSummary> Scans, bool MayDeleteScans);
