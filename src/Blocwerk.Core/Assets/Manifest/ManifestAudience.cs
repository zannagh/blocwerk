// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets.Resolution;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// The audience classes a manifest is built and cached for (design §6.4). Each class lists only what every caller in it
/// may fetch through the <c>/a/</c> routes (<see cref="AssetAccessGate"/>), so the classes differ only in video
/// thumbnails, the one kind whose grant depends on the caller.
/// </summary>
public enum ManifestAudience
{
    /// <summary>
    /// A signed-in member, setters and admins included: everything, draft boulders' thumbnails too. Members see drafts
    /// (the boulder lists flag them for every member), so no separate setters class is needed for assets.
    /// </summary>
    Member,

    /// <summary>A share-link viewer who is not a member: no draft boulder's thumbnail.</summary>
    ShareToken,

    /// <summary>
    /// A kiosk tablet, whoever it acts as: wall-level assets only, no video thumbnails at all. The tablet's partition is
    /// shared by every identity on it, so the cache holds only what the anonymous tablet itself may fetch.
    /// </summary>
    Kiosk,
}
