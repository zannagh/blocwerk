// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>A page of activity, newest first, with the Newer / Older paging of the web.</summary>
/// <param name="Items">The rows of this page.</param>
/// <param name="TotalCount">Every row there is.</param>
/// <param name="Page">This page, from 0.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record DeviceActivityPage(IReadOnlyList<DeviceActivityItem> Items, int TotalCount, int Page, int PageSize);
