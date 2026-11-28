// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>The body of <c>PUT /api/v1/walls/{w}/scan-mode</c>.</summary>
public sealed class SetScanModeRequest
{
    /// <summary>Panels or Scan.</summary>
    public string? Mode { get; set; }
}
