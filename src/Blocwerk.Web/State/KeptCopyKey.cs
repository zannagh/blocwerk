// <copyright file="KeptCopyKey.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Web.State;

/// <summary>
/// The localStorage key an editor keeps its unsaved state under across a deploy: <c>"&lt;identity&gt;|&lt;editor&gt;"</c>.
/// The identity is the signed-in user, or "anon", prefixed with the kiosk key on a tablet, so the same member at home
/// and on the wall tablet, the anonymous tablet, and the next person to sign in on a shared phone never see each
/// other's copies. deploy-drafts.js builds the same identity from <c>&lt;body data-bw-user data-bw-kiosk&gt;</c> and
/// refuses keys of any other.
/// </summary>
public static class KeptCopyKey
{
    /// <summary>The identity part, as deploy-drafts.js computes it.</summary>
    public static string Identity(Guid? userId, IKioskContext? kiosk)
    {
        var tablet = kiosk is { IsKiosk: true, KioskApiKeyId: { } key } ? $"k{key}-" : string.Empty;
        return tablet + (userId is { } id && id != Guid.Empty ? id.ToString() : "anon");
    }

    /// <summary>The full key for <paramref name="editor"/> (e.g. <c>boulder-create:&lt;wall&gt;</c>).</summary>
    public static string For(Guid? userId, IKioskContext? kiosk, string editor) => $"{Identity(userId, kiosk)}|{editor}";
}
