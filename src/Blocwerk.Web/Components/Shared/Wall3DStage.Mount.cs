// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Documents;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What the stage hands wall3d.js: a reference to the view's cached view3d document plus the small boulder overlay
/// (<see cref="View3DDocument"/>, design §6.5), so the model is fetched through the device cache rather than sent over the
/// circuit on every mount. A document the browser cannot load (wall3d-doc.js's DOC_FAILED) falls back to the whole view.
/// </summary>
public partial class Wall3DStage
{
    // wall3d-doc.js's DOC_FAILED: the cached document could not be used; the view goes over interop instead.
    private const string DocFailedPrefix = "wall3d-doc:";

    /// <summary>
    /// The share token, when the host page is a share-link route: the document's URL and its assets carry it. Without it
    /// the token the view's own URLs carry is used.
    /// </summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private View3DDocumentStore Documents { get; set; } = null!;

    private async Task<IJSObjectReference> InvokeMountAsync(IJSObjectReference wall3d, Wall3DView view, JsonElement? restore)
    {
        var options = MountOptions(restore);
        try
        {
            return await wall3d.InvokeAsync<IJSObjectReference>("mount", stage, DocumentPayload(view), options);
        }
        catch (JSException ex) when (ex.Message.Contains(DocFailedPrefix, StringComparison.Ordinal) && IsCurrent(view))
        {
            Logger.LogInformation("View3d document of wall {WallId} could not be loaded; sending the view instead ({Reason})", view.WallId, ex.Message);
            return await wall3d.InvokeAsync<IJSObjectReference>("mount", stage, view, options);
        }
    }

    private Dictionary<string, object?> DocumentPayload(Wall3DView view)
    {
        var published = Documents.Publish(view);
        var token = string.IsNullOrEmpty(ShareToken) ? published.Token : ShareToken;
        return new Dictionary<string, object?>
        {
            ["doc"] = AssetUrls.View3DDocument(view.WallId, published.Sha, token, view.BoulderId),
            ["wallId"] = view.WallId,
            ["sha"] = published.Sha,
            ["bytes"] = published.Bytes,
            ["token"] = token,
            ["overlay"] = published.Overlay,
        };
    }

    private Dictionary<string, object?> MountOptions(JsonElement? restore)
    {
        var options = new Dictionary<string, object?>
        {
            ["roleColors"] = BoulderHoldColors.Wall3DRoles,
            ["boulderFocus"] = BoulderFocus,
            ["initialPreset"] = InitialPreset,
            ["initialMode"] = InitialMode ?? "schematic",
            ["hintOnce"] = HintOnce,
            ["highlight"] = Highlight,
        };
        if (restore is { } carried)
        {
            options["restore"] = carried;
        }

        foreach (var (key, value) in ExtraOptions ?? new Dictionary<string, object?>())
        {
            options[key] = value;
        }

        return options;
    }
}
