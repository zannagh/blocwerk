// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>The body of <c>DELETE /api/v1/walls/{w}</c>: the wall's name, typed again, as the web's confirmation asks.</summary>
public sealed class DeleteWallRequest
{
    /// <summary>Must equal the wall's name exactly.</summary>
    public string? ConfirmName { get; set; }
}
