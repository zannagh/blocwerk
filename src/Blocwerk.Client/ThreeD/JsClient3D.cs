// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Client.Store;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Client.ThreeD;

/// <summary>
/// <see cref="IClient3D"/> over client-3d.js (<c>bwClient3d</c>) and client-3d-stage.js (<c>bwClient3dStage</c>), which
/// the shell loads; the requests and answers cross as JSON strings, so no interop serialiser touches them.
/// </summary>
public sealed class JsClient3D : IClient3D
{
    private readonly IJSRuntime js;

    /// <summary>Initializes a new instance of the <see cref="JsClient3D"/> class.</summary>
    /// <param name="js">The browser's JS runtime.</param>
    public JsClient3D(IJSRuntime js)
    {
        this.js = js;
    }

    /// <inheritdoc />
    public async Task<Client3DStatus> StatusAsync(string partition, Guid wallId)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("bwClient3d.statusJson", partition, wallId.ToString());
            return string.IsNullOrEmpty(json)
                ? Client3DStatus.None
                : JsonSerializer.Deserialize(json, ClientJsonContext.Default.Client3DStatus) ?? Client3DStatus.None;
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            return Client3DStatus.None;
        }
    }

    /// <inheritdoc />
    public async Task<string> MountAsync(ElementReference element, Client3DRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, ClientJsonContext.Default.Client3DRequest);
            return await js.InvokeAsync<string>("bwClient3dStage.mount", element, json);
        }
        catch (JSException)
        {
            return "unavailable";
        }
    }

    /// <inheritdoc />
    public async Task UnmountAsync(ElementReference element)
    {
        try
        {
            await js.InvokeVoidAsync("bwClient3dStage.unmount", element);
        }
        catch (JSException)
        {
            // Nothing mounted, or the page is going: the context goes with it.
        }
    }
}
