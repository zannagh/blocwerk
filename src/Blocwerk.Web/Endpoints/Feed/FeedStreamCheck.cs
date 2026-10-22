// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>What a live event means for the stream's own access.</summary>
internal enum FeedStreamCheck
{
    /// <summary>Nothing about access.</summary>
    None = 0,

    /// <summary>Access may have changed (a role, the owner, the share token, the wall itself): decide it again.</summary>
    Recheck = 1,

    /// <summary>The caller was removed or demoted (<c>access.revoked</c> reaches only its own user): end the stream.</summary>
    Revoked = 2,
}
