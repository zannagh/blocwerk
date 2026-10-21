// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// The 3D view as a content-addressed working-data document (design §6.5, <c>/api/walls/{w}/d/view3d/{sha}</c>): the
/// wall-level part of a <see cref="Wall3DView"/>, serialised as <c>wall3d.js</c> reads it over JS interop, and named by the
/// SHA-256 of those exact bytes. What belongs to one boulder or one viewer stays out of it and travels over interop in a
/// small <see cref="Wall3DOverlay"/>.
/// </summary>
/// <remarks>
/// Left out of the document: the highlighted boulder (id, name), every hold's role, the holds drawn only because that
/// boulder uses them (<see cref="Wall3DHold.BoulderOnly"/>), and the share token in the texture and splat URLs (the page
/// appends its own). So one wall's document is the same bytes for every viewer and every boulder page, and a share link,
/// a member and the kiosk tablet cache the one copy.
/// </remarks>
public static class View3DDocument
{
    /// <summary>The document kind in the route.</summary>
    public const string Kind = "view3d";

    /// <summary>The serialiser options: Blazor's interop shape (web casing, nulls written), so the JS reads both alike.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The wall-level document of <paramref name="view"/> and the boulder overlay that turns it back into the view.</summary>
    /// <param name="view">A built view, possibly with a highlighted boulder.</param>
    /// <returns>The document and its overlay.</returns>
    public static (Wall3DView Document, Wall3DOverlay Overlay) Split(Wall3DView view)
    {
        var roles = view.Holds
            .Where(h => h.Role is not null && !h.BoulderOnly)
            .ToDictionary(h => h.Id, h => h.Role!.Value);
        var boulderOnly = view.Holds.Where(h => h.BoulderOnly).ToList();
        var document = view with
        {
            BoulderId = null,
            BoulderName = null,
            Holds = view.Holds.Where(h => !h.BoulderOnly).Select(h => h.Role is null ? h : h with { Role = null }).ToList(),
            Textures = view.Textures.Select(t => t with { Url = WithoutToken(t.Url)!, MaskUrl = WithoutToken(t.MaskUrl) }).ToList(),
            SplatUrl = WithoutToken(view.SplatUrl),
            SplatMobileUrl = WithoutToken(view.SplatMobileUrl),
            SplatLevels = view.SplatLevels.Select(l => l with { Url = WithoutToken(l.Url)! }).ToList(),
        };
        return (document, new Wall3DOverlay(view.BoulderId, view.BoulderName, roles, boulderOnly));
    }

    /// <summary>The document's bytes: UTF-8 JSON, deterministic for one view.</summary>
    /// <param name="document">A document from <see cref="Split"/>.</param>
    /// <returns>The bytes the sha names.</returns>
    public static byte[] Serialize(Wall3DView document) => JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

    /// <summary>The share token the view's texture and splat URLs carry, or null (the page then supplies its own).</summary>
    /// <param name="view">A built view.</param>
    /// <returns>The unescaped token, or null.</returns>
    public static string? TokenOf(Wall3DView view)
    {
        var urls = view.Textures.Select(t => t.Url).Append(view.SplatUrl).Concat(view.SplatLevels.Select(l => l.Url));
        foreach (var url in urls)
        {
            var at = url?.IndexOf('?', StringComparison.Ordinal) ?? -1;
            var part = at < 0 ? null : url![(at + 1)..].Split('&').FirstOrDefault(p => p.StartsWith("token=", StringComparison.Ordinal));
            if (part is not null)
            {
                return Uri.UnescapeDataString(part["token=".Length..]);
            }
        }

        return null;
    }

    /// <summary><paramref name="url"/> without its <c>token</c> query parameter; every other part kept in order.</summary>
    /// <param name="url">A relative URL, or null.</param>
    /// <returns>The URL without the token.</returns>
    public static string? WithoutToken(string? url)
    {
        var at = url?.IndexOf('?', StringComparison.Ordinal) ?? -1;
        if (url is null || at < 0)
        {
            return url;
        }

        var kept = url[(at + 1)..].Split('&').Where(p => p != "token" && !p.StartsWith("token=", StringComparison.Ordinal)).ToList();
        return kept.Count == 0 ? url[..at] : $"{url[..at]}?{string.Join('&', kept)}";
    }
}
