// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What <c>bwAssetNotice.current</c> (wwwroot/js/asset-notice.js) answers for <see cref="StorageNotice"/>: the notice's
/// kind (<c>high-storage</c> or <c>install-ios</c>) and the viewer's count of walls with 3D data.
/// </summary>
public sealed record StorageNoticeState(string Kind, int WallsWith3d);
