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
    /// <summary>Rolling progression scores and charts. The default screen.</summary>
    Progress,

    /// <summary>How the app behaves for this member: home wall, zoom lens, grades, nav, notifications.</summary>
    App,

    /// <summary>E-mail, password sign-in, two-factor, linked accounts, API keys, account deletion.</summary>
    Account,

    /// <summary>The TopLogger logbook import.</summary>
    TopLogger,
}
