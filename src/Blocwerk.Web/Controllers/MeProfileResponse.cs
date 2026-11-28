// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Controllers;

/// <summary>Answer of <c>GET</c> and <c>PATCH /api/v1/me/profile</c>: the identity card and the App preferences.</summary>
/// <param name="Id">The owner's user id.</param>
/// <param name="Name">The name everyone sees: the custom display name, else the account name.</param>
/// <param name="AccountName">The name from the sign-in provider, which "Reset" goes back to.</param>
/// <param name="CustomDisplayName">The name the owner chose, or null.</param>
/// <param name="Role">User, Admin or Guest.</param>
/// <param name="MemberSince">When the account was made.</param>
/// <param name="AvatarUrl">The avatar's path (<c>/api/users/{id}/avatar</c>, no key needed), or null without one.</param>
/// <param name="HomeWallId">The wall the Home shortcut opens, or null.</param>
/// <param name="GradeScale"><c>font</c> or <c>v</c>: the scale grade entry opens on.</param>
/// <param name="Notifications">Each push notification type, true when the owner receives it.</param>
/// <param name="AppleLinked">True when an Apple ID is linked, so the app can sign in with Apple on this instance.</param>
public sealed record MeProfileResponse(
    Guid Id,
    string Name,
    string AccountName,
    string? CustomDisplayName,
    string Role,
    DateTimeOffset MemberSince,
    string? AvatarUrl,
    Guid? HomeWallId,
    string GradeScale,
    IReadOnlyDictionary<string, bool> Notifications,
    bool AppleLinked)
{
    /// <summary>The anonymous avatar route the web uses too.</summary>
    public static string? AvatarOf(User user) => user.HasAvatar ? $"/api/users/{user.Id}/avatar" : null;

    /// <summary>The answer for <paramref name="user"/>.</summary>
    public static MeProfileResponse From(User user, bool appleLinked) => new(
        user.Id,
        user.Name,
        user.DisplayName,
        string.IsNullOrWhiteSpace(user.CustomDisplayName) ? null : user.CustomDisplayName,
        user.Role.ToString(),
        user.CreatedAt,
        AvatarOf(user),
        user.HomeWallId,
        user.PreferFontGrades ? ProfilePatch.FontScale : ProfilePatch.VScale,
        ProfileNotifications.Enabled(user.DisabledNotifications),
        appleLinked);
}
