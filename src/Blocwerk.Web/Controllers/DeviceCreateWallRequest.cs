// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>The body of <c>POST /api/v1/walls</c>: the web's "+ New Wall" form, without the photo (sent after).</summary>
public sealed class DeviceCreateWallRequest
{
    /// <summary>The replay key; required, since a retried create would otherwise make a second wall.</summary>
    public Guid? ClientRequestId { get; set; }

    /// <summary>The wall's name, 1 to 256 characters.</summary>
    public string? Name { get; set; }

    /// <summary>An optional description, at most 1024 characters.</summary>
    public string? Description { get; set; }

    /// <summary>The overhang angle, 0 to 90 degrees; 0 when left out.</summary>
    public int? Angle { get; set; }
}
