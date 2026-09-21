// <copyright file="PushSub.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.ProfilePanes;

// The plain shape bwPush.getExisting returns, so DetectPushEnableAsync can tell a live subscription
// from a dropped one.
internal sealed class PushSub
{
    public string? Endpoint { get; set; }

    public string? P256dh { get; set; }

    public string? Auth { get; set; }

    public bool IsComplete =>
        !string.IsNullOrEmpty(Endpoint) && !string.IsNullOrEmpty(P256dh) && !string.IsNullOrEmpty(Auth);
}
