// <copyright file="ProfileTab.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.ProfilePanes;

/// <summary>
/// The screens the profile page is staged into. Everything here is content the app authors, so it is
/// a fixed composition sized to the viewport rather than one long scroll.
/// </summary>
public enum ProfileTab
{
    /// <summary>How the app behaves for this member: home wall, zoom lens, grades, nav, notifications, and the
    /// links to Tools (and Administration). The default screen; progression is on the Activity tab.</summary>
    App,

    /// <summary>E-mail, password sign-in, two-factor, linked accounts, API keys, account deletion.</summary>
    Account,

    /// <summary>The TopLogger logbook import.</summary>
    TopLogger,
}
