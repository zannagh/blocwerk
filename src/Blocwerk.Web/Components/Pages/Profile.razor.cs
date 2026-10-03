// <copyright file="Profile.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared.ProfilePanes;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages;

/// <summary>
/// The profile page: a persistent identity column beside one of four screens — Progress, App, Account
/// and TopLogger — for your own profile, and a reduced identity-and-progression view for anybody
/// else's. The open screen is carried in the URL so it can be linked and survives a reload.
/// </summary>
public partial class Profile
{
    [Parameter]
    public Guid? UserId { get; set; }

    [SupplyParameterFromQuery(Name = "link")]
    public string? LinkResult { get; set; }

    /// <summary>
    /// The screen to open, so a tab can be linked, bookmarked and survives a reload. Unknown or
    /// missing values fall back to <see cref="DefaultTab"/>.
    /// </summary>
    [SupplyParameterFromQuery(Name = "tab")]
    public string? TabQuery { get; set; }

    [Inject]
    private ICurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    private IWallService WallService { get; set; } = default!;

    [Inject]
    private Blocwerk.Core.Configuration.BlocwerkSettings Settings { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private Blocwerk.Core.Abstractions.IKioskContext KioskContext { get; set; } = default!;

    private const ProfileTab DefaultTab = ProfileTab.Progress;

    private static readonly ProfileTab[] Tabs =
        [ProfileTab.Progress, ProfileTab.App, ProfileTab.Account, ProfileTab.TopLogger];

    private ProfileTab _tab = DefaultTab;

    private IReadOnlyList<string> _linkedProviders = [];
    private IReadOnlyList<(string Key, string Label)> _allProviders = [];
    private string? _linkMessage;

    private User? _viewer;
    private User? _target;
    private bool _isSelf;
    private bool _available = true;
    private bool _loading = true;
    private Guid? _loadedFor;
    private IReadOnlyList<Wall> _myWalls = [];
    private string? _toast;

    protected override async Task OnParametersSetAsync()
    {
        // The same component instance is reused when navigating between profiles, so reload only when
        // the target actually changes.
        if (!_loading && _loadedFor == UserId)
        {
            // Same profile, changed query string: adopt the tab the URL asks for without reloading.
            ApplyTabFromQuery();
            return;
        }

        _loadedFor = UserId;
        await LoadAsync();
    }

    private static string TabLabel(ProfileTab tab) => tab switch
    {
        ProfileTab.App => "App",
        ProfileTab.Account => "Account",
        ProfileTab.TopLogger => "TopLogger",
        _ => "Progress",
    };

    private static ProfileTab? ParseTab(string? value) =>
        Enum.TryParse<ProfileTab>(value, ignoreCase: true, out var tab) && Tabs.Contains(tab)
            ? tab
            : null;

    /// <summary>
    /// Reads the open screen off the URL. An OAuth link round trip lands on <c>/profile?link=…</c> and
    /// the only thing worth looking at then is the linked-accounts list, so it still outranks any
    /// <c>?tab=</c>; anything unknown or missing falls back to the default screen, which is also what
    /// clears a tab when the same component instance is reused for a different profile.
    /// </summary>
    private void ApplyTabFromQuery()
    {
        _tab = LinkResultMessage(LinkResult) != null
            ? ProfileTab.Account
            : ParseTab(TabQuery) ?? DefaultTab;
    }

    /// <summary>
    /// Switches screen and writes the choice to the URL so it can be linked and survives a reload.
    /// </summary>
    /// <remarks>
    /// <c>replace</c>, not a push: a tab is a view of one page rather than a separate document, and
    /// pushing would make Back walk through every tab somebody clicked instead of leaving the page.
    /// The interactive circuit handles this navigation in place — same route, same component — so no
    /// state is lost. The link result is dropped from the URL along the way: it has been read, and
    /// leaving it there would keep forcing the Account screen back over every other tab.
    /// </remarks>
    private void SelectTab(ProfileTab tab)
    {
        if (_tab == tab)
        {
            return;
        }

        _tab = tab;
        _linkMessage = null;

        var path = Navigation.ToAbsoluteUri(Navigation.Uri).GetLeftPart(UriPartial.Path);
        Navigation.NavigateTo($"{path}?tab={tab.ToString().ToLowerInvariant()}", replace: true);
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _available = true;
        _target = null;
        _linkMessage = null;

        if (await TryResolveViewerAsync() is not { } viewer)
        {
            _loading = false;
            return;
        }

        _viewer = viewer;

        if (UserId == null || UserId == _viewer.Id)
        {
            _isSelf = true;
            _target = _viewer;
            _myWalls = await WallService.GetMyWallsAsync();
            _linkedProviders = await CurrentUserService.GetLinkedProvidersAsync();
            _allProviders = EnabledProviders();
            _linkMessage = LinkResultMessage(LinkResult);

            ApplyTabFromQuery();
        }
        else
        {
            _isSelf = false;
            var target = await CurrentUserService.GetUserByIdAsync(UserId.Value);
            if (target == null || !await WallService.UsersShareAWallAsync(_viewer.Id, UserId.Value))
            {
                _available = false;
            }
            else
            {
                _target = target;
            }
        }

        _loading = false;
    }

    // Enabled providers, in a stable display order, for the linked-accounts list.
    private List<(string Key, string Label)> EnabledProviders()
    {
        var providers = new List<(string, string)>();
        if (Settings.GitHubOAuth.Enabled)
        {
            providers.Add(("github", "GitHub"));
        }

        if (Settings.GoogleOAuth.Enabled)
        {
            providers.Add(("google", "Google"));
        }

        if (Settings.MicrosoftOAuth.Enabled)
        {
            providers.Add(("microsoft", "Microsoft"));
        }

        return providers;
    }

    private static string? LinkResultMessage(string? result) => result switch
    {
        "linked" => "Account linked.",
        "merged" => "Accounts merged — the other account was absorbed into this one.",
        "already" => "That account is already linked to this profile.",
        "error" => "Couldn't link that account. Please try again.",
        "unavailable" => "That provider isn't available.",
        "apikey" => Blocwerk.Core.Services.ApiKeySessionRestrictedException.UserMessage,
        _ => null,
    };

    private async Task ReloadSelfAsync()
    {
        if (await TryResolveViewerAsync() is not { } viewer)
        {
            return;
        }

        _viewer = viewer;
        _target = _viewer;
    }

    /// <summary>
    /// Resolves the viewer, or sends the browser to a clean sign-in prompt and returns null.
    /// </summary>
    /// <remarks>
    /// <c>[Authorize]</c> only proves the cookie is intact; it says nothing about the account behind
    /// it still existing. An account merged away (or deleted) on another device leaves this tab with
    /// a perfectly valid cookie that resolves to nobody — and an unguarded call there did not fail
    /// gracefully, it 500ed the page on every reload with no hint that signing in again would fix it.
    /// Same bounce MainLayout, Home, Join, WallDetail and BoulderDetail already make.
    /// </remarks>
    private async Task<User?> TryResolveViewerAsync()
    {
        try
        {
            return await CurrentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            Navigation.NavigateTo("/account/login", replace: true);
            return null;
        }
    }

    private async Task ShowToast(string message)
    {
        _toast = message;
        StateHasChanged();
        await Task.Delay(2500);
        _toast = null;
        StateHasChanged();
    }
}
