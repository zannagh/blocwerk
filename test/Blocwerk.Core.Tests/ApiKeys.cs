// <copyright file="ApiKeys.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Net.Http.Headers;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests;

/// <summary>API-key principals as the key handler builds them, and request bodies for controller tests.</summary>
internal static class ApiKeys
{
    /// <summary>A personal (User-scoped) key, with write access unless <paramref name="allowWrite"/> is false.</summary>
    public static ClaimsPrincipal Personal(bool allowWrite = true)
    {
        var claims = Base(ApiKeyScope.User);
        if (allowWrite)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.AllowWrite, "true"));
        }

        return Principal(claims);
    }

    /// <summary>A wall key issued for <paramref name="wallId"/>.</summary>
    public static ClaimsPrincipal Wall(Guid wallId)
    {
        var claims = Base(ApiKeyScope.Wall);
        claims.Add(new Claim(ApiKeyClaimTypes.WallId, wallId.ToString()));
        return Principal(claims);
    }

    /// <summary>Gives the controller's request a multipart/form-data body with these files.</summary>
    public static void Multipart(ControllerBase controller, params (string Name, byte[] Bytes)[] files)
    {
        using var content = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(part, "photos", name);
        }

        content.Add(new StringContent("ignored"), "note");
        var body = new MemoryStream();
        content.CopyTo(body, null, CancellationToken.None);
        body.Position = 0;
        controller.Request.Body = body;
        controller.Request.ContentType = content.Headers.ContentType!.ToString();
    }

    private static List<Claim> Base(ApiKeyScope scope) =>
    [
        new(ClaimTypes.NameIdentifier, "1"),
        new(ApiKeyClaimTypes.Scope, scope.ToString()),
        new(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
    ];

    private static ClaimsPrincipal Principal(List<Claim> claims) =>
        new(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
}
