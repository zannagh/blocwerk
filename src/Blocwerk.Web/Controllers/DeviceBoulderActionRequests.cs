// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>The bodies of the companion app's boulder actions.</summary>
public sealed class DeviceBoulderActionRequests
{
    /// <summary><c>POST …/grade-proposal</c>: a Font or V grade.</summary>
    public string? Grade { get; set; }

    /// <summary><c>PUT …/parent</c>: the boulder this one is a variant of, or null for "not a variant".</summary>
    public Guid? ParentId { get; set; }

    /// <summary><c>PUT …/variants</c>: the boulders that are variants of this one (replaces the list).</summary>
    public List<Guid>? VariantIds { get; set; }
}
