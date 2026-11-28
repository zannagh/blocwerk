// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Who the caller's personal key belongs to, that owner's avatar, and revoking the key itself: what a signed-in app
/// shows on its profile and does on sign out.
/// </summary>
/// <remarks>
/// Pinned to the API-key scheme like the other <c>/api/v1/me</c> controllers, so no browser cookie authorizes these
/// routes and antiforgery does not apply.
/// </remarks>
[ApiController]
[Route("api/v1/me")]
[Authorize(Policy = BlocwerkPolicies.UserApiKey, AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName)]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public sealed class MeAccountController : ControllerBase
{
    private const string AvatarPath = "/api/v1/me/avatar";

    private readonly IDbContextFactory<BlocwerkDbContext> contexts;
    private readonly IApiKeyService apiKeys;
    private readonly ICurrentUserService currentUser;

    public MeAccountController(
        IDbContextFactory<BlocwerkDbContext> contexts,
        IApiKeyService apiKeys,
        ICurrentUserService currentUser)
    {
        this.contexts = contexts;
        this.apiKeys = apiKeys;
        this.currentUser = currentUser;
    }

    /// <summary>The key's owner: id, name, e-mail, avatar link (null without one) and the key's own name.</summary>
    [HttpGet("account")]
    public async Task<IActionResult> GetAccount(CancellationToken ct)
    {
        if (await OwnerIdAsync() is not { } userId || User.GetApiKeyId() is not { } keyId)
        {
            return Unauthorized();
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.DisplayName, u.CustomDisplayName, u.Email, HasAvatar = u.AvatarImage != null })
            .FirstOrDefaultAsync(ct);
        var keyName = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking()
            .Where(k => k.Id == keyId)
            .Select(k => k.Name)
            .FirstOrDefaultAsync(ct);
        if (account is null)
        {
            return Unauthorized();
        }

        var name = string.IsNullOrWhiteSpace(account.CustomDisplayName) ? account.DisplayName : account.CustomDisplayName;
        return Ok(new MeAccountResponse(account.Id, name, account.Email, account.HasAvatar ? AvatarPath : null, keyName));
    }

    /// <summary>The owner's avatar bytes; 404 without one.</summary>
    [HttpGet("avatar")]
    [Produces("image/png", "image/jpeg", "image/webp", "image/gif")]
    public async Task<IActionResult> GetAvatar(CancellationToken ct)
    {
        if (await OwnerIdAsync() is not { } userId)
        {
            return Unauthorized();
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var avatar = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.AvatarImage != null)
            .Select(u => new { u.AvatarImage, u.AvatarContentType })
            .FirstOrDefaultAsync(ct);
        if (avatar?.AvatarImage is not { Length: > 0 } bytes)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=300";
        return File(bytes, avatar.AvatarContentType ?? "application/octet-stream");
    }

    /// <summary>Revokes the key this request authenticated with: the app's sign out. 204, also when already revoked.</summary>
    [HttpDelete("api-key")]
    public async Task<IActionResult> RevokeOwnKey(CancellationToken ct)
    {
        if (await OwnerIdAsync() is not { } userId || User.GetApiKeyId() is not { } keyId)
        {
            return Unauthorized();
        }

        await apiKeys.RevokeAsync(keyId, userId, ct);
        return NoContent();
    }

    /// <summary>
    /// The key's owner, resolved the way every other <c>/api/v1/me</c> route resolves it. The key principal's
    /// name-identifier is the provider's id, not the user id, so it is never parsed here.
    /// </summary>
    private async Task<Guid?> OwnerIdAsync()
    {
        try
        {
            return (await currentUser.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
